using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;

namespace BiDeploy.Agent.Core
{
    /// <summary>
    /// İstemci bilgisayardaki ajan. Yeni sürümü önceden indirir; sunucu ajanı kurulumu serbest bıraktığında
    /// Mikro'yu (gerekirse uyarıp kapatarak) sessizce günceller ve sonucu raporlar.
    /// </summary>
    public sealed class ClientAgent
    {
        private readonly AgentOptions _options;
        private readonly AgentState _state;
        private readonly HttpClient _http;
        private readonly Uri _serverUri;
        private readonly PackageCache _cache;
        private readonly IMikroInstallation _mikro;
        private readonly IProcessControl _processes;
        private readonly IInstallerRunner _installer;
        private readonly IUserNotifier _notifier;
        private readonly IClock _clock;
        private readonly IAgentLog _log;
        private readonly string _machineName;

        public ClientAgent(AgentOptions options, AgentState state, HttpClient http, PackageCache cache,
            IMikroInstallation mikro, IProcessControl processes, IInstallerRunner installer, IUserNotifier notifier,
            IClock clock, IAgentLog log, string machineName)
        {
            _options = options;
            _state = state;
            _http = http;
            _serverUri = new Uri(options.ServerUrl.TrimEnd('/') + "/");
            _cache = cache;
            _mikro = mikro;
            _processes = processes;
            _installer = installer;
            _notifier = notifier;
            _clock = clock;
            _log = log;
            _machineName = machineName;
        }

        public ClientStatus LastStatus { get; private set; } = new ClientStatus();

        public async Task RunCycleAsync(CancellationToken ct)
        {
            var status = new ClientStatus { MachineName = _machineName };
            try
            {
                var target = await GetTargetAsync(ct).ConfigureAwait(false);
                await ProcessAsync(target, status, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _log.Error("İstemci turu başarısız", ex);
                status.State = ClientState.Failed;
                status.LastError = ex.Message;
            }

            LastStatus = status;
            try
            {
                await ReportAsync(status, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _log.Error("Sunucu ajanına rapor gönderilemedi", ex);
            }
        }

        private async Task ProcessAsync(LanTargetResponse target, ClientStatus status, CancellationToken ct)
        {
            // 1) Ön hazırlık: en yeni paketi şimdiden indir, kurma.
            PackageManifest? prestaged = null;
            if (target.Prestage != null)
            {
                status.State = ClientState.Downloading;
                prestaged = await DownloadAsync(target.Prestage, ct).ConfigureAwait(false);
                status.PrestagedVersion = prestaged.Version;
            }

            // 2) Serbest bırakılmış sürüm var mı?
            var release = target.Release == null ? null : await DownloadAsync(target.Release, ct).ConfigureAwait(false);
            var mainExe = release?.MainExecutable ?? prestaged?.MainExecutable ?? ServerAgent.DefaultMainExecutable(_options.Product);
            status.InstalledVersion = _mikro.GetInstalledVersion(mainExe);
            var processName = release?.ProcessName ?? Path.GetFileNameWithoutExtension(mainExe);
            status.MikroRunning = _processes.IsRunning(processName);

            // Eski sürüme asla dönülmez: serbest bırakılan sürüm kuruludan büyük değilse bir şey yapılmaz.
            if (release == null || MikroVersion.Compare(release.Version, status.InstalledVersion) <= 0)
            {
                status.State = status.InstalledVersion == null ? ClientState.Unknown
                    : release == null && prestaged != null && MikroVersion.Compare(prestaged.Version, status.InstalledVersion) > 0
                        ? ClientState.Ready
                        : ClientState.UpToDate;
                ClearWaiting();
                return;
            }

            if (_state.LastFailedPackageId == release.PackageId && _state.LastFailedAtUtc.HasValue
                && _clock.UtcNow - _state.LastFailedAtUtc.Value < TimeSpan.FromMinutes(_options.RetryAfterFailureMinutes))
            {
                status.State = ClientState.Failed;
                status.LastError = _state.LastError;
                return;
            }

            // 3) Mikro açıksa önce uyar, süre dolunca kapat.
            if (status.MikroRunning)
            {
                if (_state.WaitingForCloseSincePackageId != release.PackageId || !_state.WaitingForCloseSinceUtc.HasValue)
                {
                    _state.WaitingForCloseSincePackageId = release.PackageId;
                    _state.WaitingForCloseSinceUtc = _clock.UtcNow;
                    SaveState();
                    var minutes = target.ForceCloseAfterMinutes;
                    _notifier.NotifyAll(
                        $"Mikro sunucusu {release.Version} sürümüne güncellendi. Mikro {minutes} dakika içinde kapatılıp güncellenecek. " +
                        "Lütfen açık işlemlerinizi kaydedip Mikro'yu kapatın.", TimeSpan.FromMinutes(minutes));
                }

                if (_clock.UtcNow - _state.WaitingForCloseSinceUtc!.Value < TimeSpan.FromMinutes(target.ForceCloseAfterMinutes))
                {
                    status.State = ClientState.WaitingForMikroToClose;
                    return;
                }

                _log.Info("Bekleme süresi doldu, Mikro kapatılıyor.");
                _processes.Kill(processName);
                status.MikroRunning = false;
            }

            // 4) Kurulum.
            status.State = ClientState.Installing;
            await ReportAsync(status, ct).ConfigureAwait(false);

            var setup = _cache.SetupPath(release);
            PackageSigning.VerifyFile(setup, release); // Kurulumdan hemen önce tam özet kontrolü.
            Directory.CreateDirectory(_options.LogsDirectory);
            var logPath = Path.Combine(_options.LogsDirectory, $"install-{release.PackageId}-{_clock.UtcNow:yyyyMMddHHmmss}.log");

            _log.Info($"Kurulum başlıyor: {release.PackageId}");
            var result = _installer.Run(setup, release.InstallerArguments, logPath, TimeSpan.FromMinutes(_options.InstallTimeoutMinutes));
            var installedAfter = _mikro.GetInstalledVersion(release.MainExecutable);
            status.InstalledVersion = installedAfter;

            if (result.Succeeded && MikroVersion.Compare(installedAfter, release.Version) >= 0)
            {
                _log.Info($"Kurulum tamamlandı: {installedAfter}");
                status.State = ClientState.UpToDate;
                _state.LastFailedPackageId = null;
                _state.LastFailedAtUtc = null;
                _state.LastError = null;
                ClearWaiting();
                SaveState();
                return;
            }

            var error = result.TimedOut
                ? $"Kurulum {_options.InstallTimeoutMinutes} dakikada bitmedi."
                : result.ExitCode != 0
                    ? $"Kurulum çıkış kodu {result.ExitCode}."
                    : $"Kurulum sonrası sürüm {installedAfter ?? "okunamadı"}, beklenen {release.Version}.";
            if (!string.IsNullOrWhiteSpace(result.LogTail)) error += " Log: " + result.LogTail;
            _log.Error("Kurulum başarısız: " + error);
            status.State = ClientState.Failed;
            status.LastError = error;
            _state.LastFailedPackageId = release.PackageId;
            _state.LastFailedAtUtc = _clock.UtcNow;
            _state.LastError = error;
            SaveState();
        }

        private Task<PackageManifest> DownloadAsync(SignedManifest signed, CancellationToken ct) =>
            _cache.EnsureDownloadedAsync(signed, _http,
                m => new Uri(_serverUri, $"lan/v1/packages/{Uri.EscapeDataString(m.PackageId)}/file"), Authorize, ct);

        private async Task<LanTargetResponse> GetTargetAsync(CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_serverUri, "lan/v1/target"));
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await VpsClient.Read<LanTargetResponse>(response).ConfigureAwait(false);
        }

        private async Task ReportAsync(ClientStatus status, CancellationToken ct)
        {
            status.ReportedAtUtc = _clock.UtcNow;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_serverUri, "lan/v1/report")) { Content = VpsClient.Json(status) };
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        private void Authorize(HttpRequestMessage request) => request.Headers.Add(LanServer.KeyHeader, _options.LanKey);

        private void ClearWaiting()
        {
            if (_state.WaitingForCloseSincePackageId == null) return;
            _state.WaitingForCloseSincePackageId = null;
            _state.WaitingForCloseSinceUtc = null;
            SaveState();
        }

        private void SaveState() => _state.Save(_options.StateFile);
    }
}

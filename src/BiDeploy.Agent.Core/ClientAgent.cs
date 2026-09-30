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
        private readonly IProcessControl _processes;
        private readonly IInstallerRunner _installer;
        private readonly IUserNotifier _notifier;
        private readonly IClock _clock;
        private readonly IAgentLog _log;
        private readonly string _machineName;

        public ClientAgent(AgentOptions options, AgentState state, HttpClient http, PackageCache cache,
            IProcessControl processes, IInstallerRunner installer, IUserNotifier notifier,
            IClock clock, IAgentLog log, string machineName)
        {
            _options = options;
            _state = state;
            _http = http;
            _serverUri = new Uri(options.ServerUrl.TrimEnd('/') + "/");
            _cache = cache;
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

            // 2) Bayi kurulumu serbest bıraktı mı? Hangi paketin kurulduğunu ajan kendisi kaydeder,
            //    Mikro'nun sürümü okunmaz; aynı paket ikinci kez kurulmaz.
            var release = target.Release == null ? null : await DownloadAsync(target.Release, ct).ConfigureAwait(false);
            status.InstalledVersion = _state.LastInstalledVersion;
            var processName = (release ?? prestaged)?.ProcessName;
            status.MikroRunning = processName != null && _processes.IsRunning(processName);

            // Bayinin duyurusu: Mikro'su açık olan bilgisayarlarda bir kez gösterilir.
            var announcement = target.Announcement;
            if (announcement != null && announcement.Id != _state.LastAnnouncementId)
            {
                if (status.MikroRunning)
                    _notifier.NotifyAll(announcement.Message, TimeSpan.FromMinutes(10));
                _state.LastAnnouncementId = announcement.Id;
                SaveState();
            }

            if (release == null || release.PackageId == _state.LastInstalledPackageId)
            {
                status.State = prestaged != null && prestaged.PackageId != _state.LastInstalledPackageId
                    ? ClientState.Ready
                    : _state.LastInstalledPackageId != null ? ClientState.UpToDate : ClientState.Unknown;
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
                        $"Mikro {release.Version} sürümüne güncellenecek. Mikro {minutes} dakika içinde kapatılacak. " +
                        "Lütfen açık işlemlerinizi kaydedip Mikro'yu kapatın.", TimeSpan.FromMinutes(minutes));
                }

                if (_clock.UtcNow - _state.WaitingForCloseSinceUtc!.Value < TimeSpan.FromMinutes(target.ForceCloseAfterMinutes))
                {
                    status.State = ClientState.WaitingForMikroToClose;
                    return;
                }

                _log.Info("Bekleme süresi doldu, Mikro kapatılıyor.");
                _processes.Kill(release.ProcessName);
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
            if (result.Succeeded)
            {
                _log.Info($"Kurulum tamamlandı: {release.Version}");
                status.State = ClientState.UpToDate;
                status.InstalledVersion = release.Version;
                _state.LastInstalledPackageId = release.PackageId;
                _state.LastInstalledVersion = release.Version;
                _state.LastFailedPackageId = null;
                _state.LastFailedAtUtc = null;
                _state.LastError = null;
                ClearWaiting();
                SaveState();
                return;
            }

            var error = result.TimedOut
                ? $"Kurulum {_options.InstallTimeoutMinutes} dakikada bitmedi."
                : $"Kurulum çıkış kodu {result.ExitCode}.";
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

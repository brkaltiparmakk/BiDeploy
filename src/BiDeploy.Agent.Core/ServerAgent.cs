using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;

namespace BiDeploy.Agent.Core
{
    /// <summary>
    /// Mikro sunucusundaki ajan. Paketleri VPS'ten bir kez indirir (ön hazırlık), istemcilere yerel ağdan dağıtır
    /// ve sunucudaki Mikro güncellendiğinde istemci kurulumunu serbest bırakır.
    /// </summary>
    public sealed class ServerAgent
    {
        private readonly AgentOptions _options;
        private readonly AgentState _state;
        private readonly VpsClient _vps;
        private readonly PackageCache _cache;
        private readonly IMikroInstallation _mikro;
        private readonly IClock _clock;
        private readonly IAgentLog _log;
        private readonly ConcurrentDictionary<string, ClientStatus> _clients =
            new ConcurrentDictionary<string, ClientStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly object _stateLock = new object();

        public ServerAgent(AgentOptions options, AgentState state, VpsClient vps, PackageCache cache,
            IMikroInstallation mikro, IClock clock, IAgentLog log)
        {
            _options = options;
            _state = state;
            _vps = vps;
            _cache = cache;
            _mikro = mikro;
            _clock = clock;
            _log = log;
            _vps.DeviceToken = state.DeviceToken;
        }

        public string? ServerMikroVersion { get; private set; }

        public async Task ActivateAsync(string activationCode, string vkn, string machineId, string machineName, CancellationToken ct)
        {
            if (!TaxId.IsValid(vkn)) throw new ArgumentException("VKN/TCKN geçersiz.", nameof(vkn));
            var response = await _vps.ActivateAsync(new ActivationRequest
            {
                ActivationCode = activationCode.Trim(),
                Vkn = vkn.Trim(),
                MachineId = machineId,
                MachineName = machineName,
            }, ct).ConfigureAwait(false);

            lock (_stateLock)
            {
                _state.DeviceToken = response.DeviceToken;
                _state.CompanyTitle = response.CompanyTitle;
                _state.LicenseExpiresAtUtc = response.LicenseExpiresAtUtc;
                SaveState();
            }
            _vps.DeviceToken = response.DeviceToken;
            _log.Info($"Etkinleştirildi: {response.CompanyTitle}, lisans bitişi {response.LicenseExpiresAtUtc:yyyy-MM-dd}");
        }

        /// <summary>Bir tur: VPS'ten yeni sürümü sor, önceden indir, sunucu sürümünü algıla, VPS'e rapor et.</summary>
        public async Task RunCycleAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_state.DeviceToken))
            {
                _log.Error("Ajan etkinleştirilmemiş. 'BiDeploy.Agent.exe activate <kod> <vkn>' komutunu çalıştırın.");
                return;
            }

            await PrestageLatestAsync(ct).ConfigureAwait(false);
            DetectServerVersion();
            if (_options.AutoRelease) TryAutoRelease();
            _cache.Prune(_state.PrestagedPackageId, _state.ReleasedPackageId);

            try
            {
                await _vps.ReportAsync(BuildReport(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _log.Error("VPS'e rapor gönderilemedi", ex);
            }
        }

        private async Task PrestageLatestAsync(CancellationToken ct)
        {
            LatestPackageResponse latest;
            try
            {
                latest = await _vps.GetLatestAsync(_options.Product, _options.Architecture, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // VPS'e ulaşılamasa da ajan elindeki paketlerle çalışmaya devam eder.
                _log.Error("VPS'e ulaşılamadı, yerel paketlerle devam ediliyor", ex);
                return;
            }

            lock (_stateLock) _state.LicenseExpiresAtUtc = latest.LicenseExpiresAtUtc;

            if (latest.LicenseExpired)
            {
                _log.Error("Lisans süresi dolmuş: yeni sürüm indirilmiyor. Mevcut kurulumlar etkilenmez.");
                return;
            }
            if (latest.Package == null) return;

            try
            {
                var manifest = await _cache.EnsureDownloadedAsync(
                    latest.Package, _vps.Http, m => _vps.PackageFileUrl(m.PackageId), _vps.Authorize, ct).ConfigureAwait(false);
                lock (_stateLock)
                {
                    if (_state.PrestagedPackageId != manifest.PackageId)
                    {
                        _log.Info($"Yeni sürüm önceden indirildi: {manifest.PackageId}");
                        _state.PrestagedPackageId = manifest.PackageId;
                        SaveState();
                    }
                }
            }
            catch (PackageVerificationException ex)
            {
                _log.Error("Paket doğrulanamadı, kullanılmayacak", ex);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _log.Error("Paket indirilemedi, sonraki turda kaldığı yerden devam edilecek", ex);
            }
        }

        private void DetectServerVersion()
        {
            var mainExe = CurrentManifest()?.MainExecutable ?? DefaultMainExecutable(_options.Product);
            try
            {
                ServerMikroVersion = _mikro.GetInstalledVersion(mainExe);
            }
            catch (Exception ex)
            {
                _log.Error("Sunucudaki Mikro sürümü okunamadı", ex);
                ServerMikroVersion = null;
            }
        }

        internal static string DefaultMainExecutable(string product) => "Mikro" + product + ".exe";

        private PackageManifest? CurrentManifest()
        {
            var signed = _cache.TryGetReady(_state.PrestagedPackageId) ?? _cache.TryGetReady(_state.ReleasedPackageId);
            return signed == null ? null : PackageManifest.Parse(signed.ManifestBytes());
        }

        /// <summary>
        /// "İstemcileri Güncelle": sunucudaki Mikro sürümüne karşılık gelen paketi istemcilere serbest bırakır.
        /// Sunucu henüz güncellenmediyse veya paket hazır değilse hata verir.
        /// </summary>
        public PackageManifest ReleaseForClients()
        {
            DetectServerVersion();
            if (ServerMikroVersion == null)
                throw new InvalidOperationException("Sunucudaki Mikro sürümü okunamadı; önce sunucuyu güncelleyin veya MikroExePath ayarını kontrol edin.");

            var package = MatchingPackage(ServerMikroVersion);
            if (package == null)
                throw new InvalidOperationException(
                    $"Sunucu sürümü {ServerMikroVersion}, ancak bu sürümün istemci paketi hazır değil" +
                    (_state.PrestagedPackageId != null ? $" (hazır olan: {_state.PrestagedPackageId})." : "."));

            lock (_stateLock)
            {
                if (_state.ReleasedPackageId != package.PackageId)
                {
                    _state.ReleasedPackageId = package.PackageId;
                    _state.ReleasedAtUtc = _clock.UtcNow;
                    SaveState();
                    _log.Info($"İstemci güncellemesi serbest bırakıldı: {package.PackageId}");
                }
            }
            return package;
        }

        private void TryAutoRelease()
        {
            if (ServerMikroVersion == null) return;
            var package = MatchingPackage(ServerMikroVersion);
            if (package != null && package.PackageId != _state.ReleasedPackageId) ReleaseForClients();
        }

        private PackageManifest? MatchingPackage(string serverVersion)
        {
            foreach (var id in new[] { _state.PrestagedPackageId, _state.ReleasedPackageId })
            {
                var signed = _cache.TryGetReady(id);
                if (signed == null) continue;
                var manifest = PackageManifest.Parse(signed.ManifestBytes());
                if (MikroVersion.AreEqual(manifest.Version, serverVersion)) return manifest;
            }
            return null;
        }

        // ---- Yerel ağ tarafı ----

        public LanTargetResponse GetLanTarget()
        {
            lock (_stateLock)
            {
                return new LanTargetResponse
                {
                    Prestage = _cache.TryGetReady(_state.PrestagedPackageId),
                    Release = _cache.TryGetReady(_state.ReleasedPackageId),
                    ReleasedAtUtc = _state.ReleasedAtUtc,
                    ForceCloseAfterMinutes = _options.ForceCloseAfterMinutes,
                };
            }
        }

        /// <summary>İstemcilere sadece şu an dağıtımda olan paketlerin dosyası verilir.</summary>
        public string? GetServableSetupPath(string packageId)
        {
            string? prestaged, released;
            lock (_stateLock)
            {
                prestaged = _state.PrestagedPackageId;
                released = _state.ReleasedPackageId;
            }
            if (packageId != prestaged && packageId != released) return null;
            var signed = _cache.TryGetReady(packageId);
            return signed == null ? null : _cache.SetupPath(PackageManifest.Parse(signed.ManifestBytes()));
        }

        public void AcceptClientReport(ClientStatus status)
        {
            if (string.IsNullOrWhiteSpace(status.MachineName)) return;
            status.ReportedAtUtc = _clock.UtcNow;
            _clients[status.MachineName] = status;
        }

        public ServerReport BuildReport()
        {
            lock (_stateLock)
            {
                return new ServerReport
                {
                    AgentVersion = typeof(ServerAgent).Assembly.GetName().Version?.ToString() ?? "",
                    ServerMikroVersion = ServerMikroVersion,
                    PrestagedVersion = VersionOf(_state.PrestagedPackageId),
                    ReleasedVersion = VersionOf(_state.ReleasedPackageId),
                    Clients = _clients.Values.OrderBy(c => c.MachineName).ToList(),
                };
            }
        }

        private string? VersionOf(string? packageId)
        {
            var signed = _cache.TryGetReady(packageId);
            return signed == null ? null : PackageManifest.Parse(signed.ManifestBytes()).Version;
        }

        private void SaveState() => _state.Save(_options.StateFile);
    }
}

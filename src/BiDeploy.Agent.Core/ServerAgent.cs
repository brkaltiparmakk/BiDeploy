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
        private readonly IClock _clock;
        private readonly IAgentLog _log;
        private readonly ConcurrentDictionary<string, ClientStatus> _clients =
            new ConcurrentDictionary<string, ClientStatus>(StringComparer.OrdinalIgnoreCase);
        private readonly object _stateLock = new object();
        private readonly SemaphoreSlim _checkNow = new SemaphoreSlim(0, 1);
        private LanAnnouncement? _announcement;

        /// <summary>VPS'e son başarılı erişim ve son hata; sunucu ekranında gösterilir.</summary>
        public DateTime? LastVpsContactUtc { get; private set; }
        public string? LastVpsError { get; private set; }
        public string? CompanyTitle => _state.CompanyTitle;
        public DateTime? LicenseExpiresAtUtc => _state.LicenseExpiresAtUtc;
        public bool IsActivated => !string.IsNullOrEmpty(_state.DeviceToken);

        public ServerAgent(AgentOptions options, AgentState state, VpsClient vps, PackageCache cache,
            IClock clock, IAgentLog log)
        {
            _options = options;
            _state = state;
            _vps = vps;
            _cache = cache;
            _clock = clock;
            _log = log;
            _vps.DeviceToken = state.DeviceToken;
        }

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

        /// <summary>Bir tur: VPS'ten yeni sürümü sor, önceden indir, VPS'e rapor et.</summary>
        public async Task RunCycleAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_state.DeviceToken))
            {
                _log.Error("Ajan etkinleştirilmemiş. 'BiDeploy.Agent.exe activate <kod> <vkn>' komutunu çalıştırın.");
                return;
            }

            await PrestageLatestAsync(ct).ConfigureAwait(false);
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
                LastVpsContactUtc = _clock.UtcNow;
                LastVpsError = null;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                LastVpsError = ex.Message;
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

        /// <summary>
        /// "İstemcileri Güncelle": bayi Mikro sunucusunu güncelledikten sonra bu komutu verir; önceden indirilmiş
        /// paket istemcilere serbest bırakılır. Hangi sürümün ne zaman kurulacağına bayi karar verir.
        /// </summary>
        public PackageManifest ReleaseForClients()
        {
            lock (_stateLock)
            {
                var signed = _cache.TryGetReady(_state.PrestagedPackageId)
                    ?? throw new InvalidOperationException("Dağıtıma hazır paket yok; sunucu ajanı yeni sürümü henüz indirmedi.");
                var package = PackageManifest.Parse(signed.ManifestBytes());
                if (_state.ReleasedPackageId != package.PackageId)
                {
                    _state.ReleasedPackageId = package.PackageId;
                    _state.ReleasedAtUtc = _clock.UtcNow;
                    SaveState();
                    _log.Info($"İstemci güncellemesi serbest bırakıldı: {package.PackageId}");
                }
                return package;
            }
        }

        /// <summary>Sunucu ekranındaki "Şimdi kontrol et": bekleme süresini beklemeden bir tur çalıştırır.</summary>
        public void RequestCheckNow()
        {
            try { _checkNow.Release(); } catch (SemaphoreFullException) { /* zaten istenmiş */ }
        }

        /// <summary>Bir sonraki tura kadar bekler; "Şimdi kontrol et" istenirse erken döner.</summary>
        public async Task WaitForNextCycleAsync(TimeSpan interval, CancellationToken ct)
        {
            await _checkNow.WaitAsync(interval, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Mikro'su açık tüm istemcilerde gösterilecek duyuru. Bayi sunucuyu güncellemeden önce kullanıcıların
        /// Mikro'dan çıkmasını istemek için kullanır.
        /// </summary>
        public LanAnnouncement Announce(string message, TimeSpan validFor)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("Mesaj boş olamaz.", nameof(message));
            var announcement = new LanAnnouncement
            {
                Id = Guid.NewGuid().ToString("N"),
                Message = message.Trim(),
                ExpiresAtUtc = _clock.UtcNow + validFor,
            };
            lock (_stateLock) _announcement = announcement;
            _log.Info("Duyuru gönderildi: " + announcement.Message);
            return announcement;
        }

        public System.Collections.Generic.IReadOnlyList<ClientStatus> Clients =>
            _clients.Values.OrderBy(c => c.MachineName).ToList();

        public DateTime Now => _clock.UtcNow;

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
                    Announcement = _announcement != null && _announcement.ExpiresAtUtc > _clock.UtcNow ? _announcement : null,
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

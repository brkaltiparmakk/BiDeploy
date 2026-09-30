using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using BiDeploy.Core;

namespace BiDeploy.Agent.Core
{
    /// <summary>
    /// İmzası doğrulanmış paketlerin yerel deposu: packages/{paketId}/manifest.json, manifest.sig ve setup dosyası.
    /// Bir paket ancak manifest imzası ve dosya özeti doğrulandıktan sonra "hazır" sayılır.
    /// </summary>
    public sealed class PackageCache
    {
        private readonly string _root;
        private readonly PackageSigning.KeyFile _trustedKey;

        public PackageCache(string root, PackageSigning.KeyFile trustedKey)
        {
            _root = root;
            _trustedKey = trustedKey;
        }

        private string PackageDir(string packageId) => Path.Combine(_root, packageId);

        public string SetupPath(PackageManifest manifest) => Path.Combine(PackageDir(manifest.PackageId), manifest.FileName);

        /// <summary>Paket önbellekte tam ve doğrulanmış olarak varsa imzalı manifestini döner.</summary>
        public SignedManifest? TryGetReady(string? packageId)
        {
            if (string.IsNullOrEmpty(packageId)) return null;
            var dir = PackageDir(packageId!);
            var manifestPath = Path.Combine(dir, "manifest.json");
            var sigPath = Path.Combine(dir, "manifest.sig");
            var readyMarker = Path.Combine(dir, ".ready");
            if (!File.Exists(manifestPath) || !File.Exists(sigPath) || !File.Exists(readyMarker)) return null;

            var signed = new SignedManifest { ManifestJson = File.ReadAllText(manifestPath), Signature = File.ReadAllText(sigPath).Trim() };
            try
            {
                var manifest = PackageSigning.VerifyManifest(signed, _trustedKey);
                var setup = SetupPath(manifest);
                // Tam özet her seferinde hesaplanmaz (300 MB); boyut kontrolü yeterli, özet indirmede ve kurulumdan önce doğrulanır.
                if (manifest.PackageId != packageId || !File.Exists(setup) || new FileInfo(setup).Length != manifest.FileSize)
                    return null;
                return signed;
            }
            catch (PackageVerificationException)
            {
                return null;
            }
        }

        /// <summary>
        /// İmzalı manifesti doğrular, setup dosyasını (kaldığı yerden devam ederek) indirir ve özetini kontrol eder.
        /// </summary>
        public async Task<PackageManifest> EnsureDownloadedAsync(
            SignedManifest signed, HttpClient http, Func<PackageManifest, Uri> fileUrl, Action<HttpRequestMessage>? authorize, CancellationToken ct)
        {
            var manifest = PackageSigning.VerifyManifest(signed, _trustedKey);
            if (TryGetReady(manifest.PackageId) != null) return manifest;

            var dir = PackageDir(manifest.PackageId);
            Directory.CreateDirectory(dir);
            var setup = SetupPath(manifest);
            var partial = setup + ".partial";

            long existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (existing > manifest.FileSize)
            {
                File.Delete(partial);
                existing = 0;
            }

            if (existing < manifest.FileSize)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl(manifest));
                authorize?.Invoke(request);
                if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var append = existing > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var target = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target, 81920, ct).ConfigureAwait(false);
            }

            try
            {
                PackageSigning.VerifyFile(partial, manifest);
            }
            catch (PackageVerificationException)
            {
                File.Delete(partial);
                throw;
            }

            if (File.Exists(setup)) File.Delete(setup);
            File.Move(partial, setup);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), signed.ManifestJson);
            File.WriteAllText(Path.Combine(dir, "manifest.sig"), signed.Signature);
            File.WriteAllText(Path.Combine(dir, ".ready"), DateTime.UtcNow.ToString("o"));
            return manifest;
        }

        /// <summary>Verilen paketler dışındaki eski paketleri siler (disk dolmasın).</summary>
        public void Prune(params string?[] keepPackageIds)
        {
            if (!Directory.Exists(_root)) return;
            foreach (var dir in Directory.GetDirectories(_root))
            {
                var id = Path.GetFileName(dir);
                if (Array.IndexOf(keepPackageIds, id) >= 0) continue;
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { /* kullanımda olabilir, sonraki turda tekrar denenir */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

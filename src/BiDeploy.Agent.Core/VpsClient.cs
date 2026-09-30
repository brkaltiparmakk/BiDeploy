using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;

namespace BiDeploy.Agent.Core
{
    /// <summary>Sunucu ajanının VPS ile konuşması. Bağlantıyı her zaman ajan başlatır (müşteride port açılmaz).</summary>
    public sealed class VpsClient
    {
        private readonly HttpClient _http;
        private readonly Uri _baseUri;

        public VpsClient(HttpClient http, string vpsUrl)
        {
            _http = http;
            _baseUri = new Uri(vpsUrl.TrimEnd('/') + "/");
        }

        public string? DeviceToken { get; set; }

        public HttpClient Http => _http;

        public Uri PackageFileUrl(string packageId) => new Uri(_baseUri, $"api/agent/packages/{Uri.EscapeDataString(packageId)}/file");

        public void Authorize(HttpRequestMessage request)
        {
            if (string.IsNullOrEmpty(DeviceToken)) throw new InvalidOperationException("Ajan etkinleştirilmemiş.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DeviceToken);
        }

        public async Task<ActivationResponse> ActivateAsync(ActivationRequest body, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/agent/activate")) { Content = Json(body) };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Etkinleştirme başarısız: " + await ErrorText(response).ConfigureAwait(false));
            return await Read<ActivationResponse>(response).ConfigureAwait(false);
        }

        public async Task<LatestPackageResponse> GetLatestAsync(string product, string architecture, CancellationToken ct)
        {
            var url = new Uri(_baseUri, $"api/agent/packages/latest?product={Uri.EscapeDataString(product)}&arch={Uri.EscapeDataString(architecture)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Paket bilgisi alınamadı: " + await ErrorText(response).ConfigureAwait(false));
            return await Read<LatestPackageResponse>(response).ConfigureAwait(false);
        }

        public async Task ReportAsync(ServerReport report, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/agent/report")) { Content = Json(report) };
            Authorize(request);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Rapor gönderilemedi: " + await ErrorText(response).ConfigureAwait(false));
        }

        internal static StringContent Json<T>(T body) =>
            new StringContent(JsonSerializer.Serialize(body, JsonDefaults.Options), Encoding.UTF8, "application/json");

        internal static async Task<T> Read<T>(HttpResponseMessage response)
        {
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return JsonSerializer.Deserialize<T>(text, JsonDefaults.Options) ?? throw new InvalidOperationException("Boş yanıt.");
        }

        private static async Task<string> ErrorText(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return $"{(int)response.StatusCode} {response.StatusCode}" + (string.IsNullOrWhiteSpace(body) ? "" : " - " + body);
        }
    }
}

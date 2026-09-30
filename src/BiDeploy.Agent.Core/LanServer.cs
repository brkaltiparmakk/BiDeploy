using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;

namespace BiDeploy.Agent.Core
{
    /// <summary>
    /// Sunucu ajanının yerel ağda istemcilere hizmet verdiği küçük HTTP sunucusu.
    /// Paketler imzalı olduğu için içerik bütünlüğü taşıma katmanına bağlı değildir; firma anahtarı yetkisiz erişimi engeller.
    /// </summary>
    public sealed class LanServer : IDisposable
    {
        public const string KeyHeader = "X-BiDeploy-Key";

        private readonly HttpListener _listener = new HttpListener();
        private readonly ServerAgent _agent;
        private readonly byte[] _lanKey;
        private readonly IAgentLog _log;

        public LanServer(string prefix, string lanKey, ServerAgent agent, IAgentLog log)
        {
            _listener.Prefixes.Add(prefix);
            _lanKey = Encoding.UTF8.GetBytes(lanKey);
            _agent = agent;
            _log = log;
        }

        public void Start() => _listener.Start();

        public async Task RunAsync(CancellationToken ct)
        {
            using (ct.Register(() => { try { _listener.Stop(); } catch (ObjectDisposedException) { } }))
            {
                while (!ct.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (Exception) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (HttpListenerException ex)
                    {
                        _log.Error("Yerel ağ dinleyicisi hatası", ex);
                        continue;
                    }
                    _ = Task.Run(() => HandleAsync(context, ct));
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                if (!IsAuthorized(request.Headers[KeyHeader]))
                {
                    response.StatusCode = 401;
                    return;
                }

                var path = request.Url?.AbsolutePath ?? "";
                if (request.HttpMethod == "GET" && path == "/lan/v1/target")
                {
                    await WriteJson(response, _agent.GetLanTarget()).ConfigureAwait(false);
                }
                else if (request.HttpMethod == "POST" && path == "/lan/v1/report")
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    var status = JsonSerializer.Deserialize<ClientStatus>(await reader.ReadToEndAsync().ConfigureAwait(false), JsonDefaults.Options);
                    if (status != null) _agent.AcceptClientReport(status);
                    response.StatusCode = 204;
                }
                else if (request.HttpMethod == "GET" && path.StartsWith("/lan/v1/packages/", StringComparison.Ordinal) && path.EndsWith("/file", StringComparison.Ordinal))
                {
                    var id = Uri.UnescapeDataString(path.Substring("/lan/v1/packages/".Length, path.Length - "/lan/v1/packages/".Length - "/file".Length));
                    var file = _agent.GetServableSetupPath(id);
                    if (file == null) response.StatusCode = 404;
                    else await ServeFile(request, response, file, ct).ConfigureAwait(false);
                }
                // Sadece aynı makineden: "İstemcileri Güncelle" komutu ve durum sorgusu (CLI / ileride tepsi uygulaması).
                else if (request.IsLocal && request.HttpMethod == "POST" && path == "/local/v1/release")
                {
                    try
                    {
                        var released = _agent.ReleaseForClients();
                        await WriteJson(response, new { packageId = released.PackageId, version = released.Version }).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        response.StatusCode = 409;
                        await WriteJson(response, new { error = ex.Message }).ConfigureAwait(false);
                    }
                }
                else if (request.IsLocal && request.HttpMethod == "GET" && path == "/local/v1/status")
                {
                    await WriteJson(response, _agent.BuildReport()).ConfigureAwait(false);
                }
                else
                {
                    response.StatusCode = 404;
                }
            }
            catch (Exception ex)
            {
                _log.Error("Yerel ağ isteği işlenemedi", ex);
                try { response.StatusCode = 500; } catch (InvalidOperationException) { }
            }
            finally
            {
                try { response.Close(); } catch (Exception) { /* istemci bağlantıyı kesmiş olabilir */ }
            }
        }

        private bool IsAuthorized(string? key)
        {
            if (key == null) return false;
            var given = Encoding.UTF8.GetBytes(key);
            if (given.Length != _lanKey.Length) return false;
            var diff = 0;
            for (var i = 0; i < given.Length; i++) diff |= given[i] ^ _lanKey[i];
            return diff == 0;
        }

        private static async Task WriteJson<T>(HttpListenerResponse response, T body)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonDefaults.Options);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        /// <summary>"bytes=N-" biçimindeki Range isteğini destekler; yarıda kalan indirme kaldığı yerden devam eder.</summary>
        private static async Task ServeFile(HttpListenerRequest request, HttpListenerResponse response, string path, CancellationToken ct)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long start = 0;
            var range = request.Headers["Range"];
            if (range != null && range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith("-", StringComparison.Ordinal)
                && long.TryParse(range.Substring(6, range.Length - 7), out var parsed) && parsed >= 0 && parsed < stream.Length)
            {
                start = parsed;
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {start}-{stream.Length - 1}/{stream.Length}");
            }
            response.ContentType = "application/octet-stream";
            response.ContentLength64 = stream.Length - start;
            stream.Seek(start, SeekOrigin.Begin);
            await stream.CopyToAsync(response.OutputStream, 81920, ct).ConfigureAwait(false);
        }

        public void Dispose() => ((IDisposable)_listener).Dispose();
    }
}

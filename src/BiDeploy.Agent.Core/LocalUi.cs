using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;

namespace BiDeploy.Agent.Core
{
    /// <summary>
    /// Mikro sunucusundaki bayi ekranı (http://localhost:8765/ui). Yalnızca sunucunun kendisinden açılır ve
    /// agent.json'daki AdminPassword ile korunur; terminal server kullanıcıları ve istemciler güncelleme tetikleyemez.
    /// </summary>
    public sealed class LocalUi
    {
        public const string CookieName = "bideploy_ui";
        public const string DefaultAnnouncement =
            "Mikro sunucusu güncellenecek. Lütfen açık işlemlerinizi kaydedip Mikro'dan çıkın.";

        private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
        private readonly ServerAgent _agent;
        private readonly AgentOptions _options;
        private volatile string? _lastError;
        private readonly ConcurrentDictionary<string, DateTime> _sessions = new ConcurrentDictionary<string, DateTime>();

        public LocalUi(ServerAgent agent, AgentOptions options)
        {
            _agent = agent;
            _options = options;
        }

        public static bool Handles(string path) => path == "/ui" || path.StartsWith("/ui/", StringComparison.Ordinal);

        public async Task HandleAsync(HttpListenerRequest request, HttpListenerResponse response)
        {
            var path = request.Url?.AbsolutePath ?? "";
            if (request.HttpMethod == "GET" && path == "/ui")
            {
                await Html(response, IsSignedIn(request) ? Dashboard(request.QueryString) : Login(failed: false)).ConfigureAwait(false);
                return;
            }
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 404;
                return;
            }

            var form = await ReadForm(request).ConfigureAwait(false);
            if (path == "/ui/login")
            {
                if (!PasswordMatches(form["password"]))
                {
                    await Task.Delay(1000).ConfigureAwait(false); // tahmin denemelerini yavaşlat
                    await Html(response, Login(failed: true)).ConfigureAwait(false);
                    return;
                }
                var token = NewToken();
                _sessions[token] = _agent.Now + SessionLifetime;
                response.AppendHeader("Set-Cookie", $"{CookieName}={token}; Path=/ui; HttpOnly; SameSite=Strict");
                Redirect(response, "/ui");
                return;
            }

            if (!IsSignedIn(request))
            {
                Redirect(response, "/ui");
                return;
            }

            switch (path)
            {
                case "/ui/logout":
                    var cookie = request.Cookies[CookieName];
                    if (cookie != null) _sessions.TryRemove(cookie.Value, out _);
                    response.AppendHeader("Set-Cookie", $"{CookieName}=; Path=/ui; Max-Age=0");
                    Redirect(response, "/ui");
                    break;
                case "/ui/announce":
                    var message = string.IsNullOrWhiteSpace(form["message"]) ? DefaultAnnouncement : form["message"]!;
                    _agent.Announce(message, TimeSpan.FromMinutes(30));
                    Redirect(response, "/ui?m=announced");
                    break;
                case "/ui/release":
                    try
                    {
                        var released = _agent.ReleaseForClients();
                        Redirect(response, "/ui?m=released");
                    }
                    catch (InvalidOperationException ex)
                    {
                        _lastError = ex.Message;
                        Redirect(response, "/ui?m=error");
                    }
                    break;
                case "/ui/check":
                    _agent.RequestCheckNow();
                    Redirect(response, "/ui?m=checked");
                    break;
                default:
                    response.StatusCode = 404;
                    break;
            }
        }

        private bool IsSignedIn(HttpListenerRequest request)
        {
            var cookie = request.Cookies[CookieName];
            if (cookie == null || !_sessions.TryGetValue(cookie.Value, out var expires)) return false;
            if (expires < _agent.Now)
            {
                _sessions.TryRemove(cookie.Value, out _);
                return false;
            }
            return true;
        }

        private bool PasswordMatches(string? given)
        {
            var a = Encoding.UTF8.GetBytes(given ?? "");
            var b = Encoding.UTF8.GetBytes(_options.AdminPassword);
            var diff = a.Length ^ b.Length;
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0 && b.Length > 0;
        }

        private static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Hashing.ToHex(bytes);
        }

        // ---------------- Sayfalar ----------------

        private string Login(bool failed) => Page("Giriş", $@"
<div class=""card narrow"">
  <h1>BiDeploy sunucu ekranı</h1>
  <p class=""muted"">{E(_agent.CompanyTitle ?? "Etkinleştirilmemiş sunucu")}</p>
  {(failed ? "<div class=\"flash bad\">Şifre hatalı.</div>" : "")}
  <form method=""post"" action=""/ui/login"">
    <label for=""password"">Yönetici şifresi</label>
    <input id=""password"" name=""password"" type=""password"" autofocus required>
    <button type=""submit"">Giriş</button>
  </form>
</div>", refresh: false);

        private string Dashboard(NameValueCollection query)
        {
            var now = _agent.Now;
            var target = _agent.GetLanTarget();
            var prestaged = target.Prestage == null ? null : PackageManifest.Parse(target.Prestage.ManifestBytes());
            var released = target.Release == null ? null : PackageManifest.Parse(target.Release.ManifestBytes());
            var clients = _agent.Clients;
            var offlineAfter = TimeSpan.FromSeconds(Math.Max(3 * _options.ClientPollSeconds, 120));
            bool Online(ClientStatus c) => now - c.ReportedAtUtc < offlineAfter;
            var online = clients.Where(Online).ToList();
            var mikroOpen = online.Where(c => c.MikroRunning).Select(c => c.MachineName).ToList();
            var ready = online.Count(c => prestaged != null && c.PrestagedVersion == prestaged.Version);
            var upToDate = online.Count(c => c.State == ClientState.UpToDate && released != null && c.InstalledVersion == released.Version);
            var failed = clients.Count(c => c.State == ClientState.Failed);
            var releaseInProgress = released != null && upToDate < online.Count;

            var sb = new StringBuilder();
            switch (query["m"])
            {
                case "announced": Flash(sb, true, "Uyarı gönderildi. Mikro'su açık bilgisayarlarda bir sonraki kontrolde (en geç 1 dakika) görünecek."); break;
                case "released": Flash(sb, true, $"{released?.Version} istemcilere dağıtılıyor. İlerlemeyi aşağıdaki tablodan izleyebilirsiniz."); break;
                case "checked": Flash(sb, true, "Yeni sürüm kontrolü başlatıldı."); break;
                case "error": Flash(sb, false, _lastError ?? "İşlem yapılamadı."); break;
            }

            // Üst bilgi: firma, lisans, VPS bağlantısı
            var license = _agent.LicenseExpiresAtUtc;
            var daysLeft = license == null ? (int?)null : (int)Math.Ceiling((license.Value - now).TotalDays);
            sb.Append($@"
<div class=""row head"">
  <div><h1>{E(_agent.CompanyTitle ?? "Etkinleştirilmemiş sunucu")}</h1>
    <div class=""muted"">Lisans: {(daysLeft == null ? "—" : daysLeft <= 0 ? "<span class=\"pill bad\">Süresi doldu</span>" : daysLeft <= 30 ? $"<span class=\"pill warn\">{daysLeft} gün kaldı</span>" : $"{daysLeft} gün")}
    · Merkez: {(_agent.LastVpsError != null ? $"<span class=\"pill bad\" title=\"{E(_agent.LastVpsError)}\">bağlanılamıyor</span>" : _agent.LastVpsContactUtc != null ? $"<span class=\"pill ok\">bağlı</span> <span class=\"muted\">son kontrol {Ago(now - _agent.LastVpsContactUtc.Value)}</span>" : "<span class=\"pill\">henüz kontrol edilmedi</span>")}</div>
  </div>
  <div class=""row"">
    <form method=""post"" action=""/ui/check""><button class=""secondary"" type=""submit"">Şimdi kontrol et</button></form>
    <form method=""post"" action=""/ui/logout""><button class=""link"" type=""submit"">Çıkış</button></form>
  </div>
</div>");

            if (!_agent.IsActivated)
                sb.Append("<div class=\"flash bad\">Sunucu ajanı etkinleştirilmemiş. Komut isteminde: <code>BiDeploy.Agent.exe activate &lt;KOD&gt; &lt;VKN&gt;</code></div>");

            sb.Append($@"
<div class=""stats"">
  <div class=""stat""><b>{E(prestaged?.Version ?? "—")}</b><span>Sunucuya indirilen yeni sürüm</span></div>
  <div class=""stat""><b>{(prestaged == null ? "—" : $"{ready}/{online.Count}")}</b><span>Yeni sürümü hazır bekleyen istemci</span></div>
  <div class=""stat""><b>{E(released?.Version ?? "—")}</b><span>Dağıtımı başlatılan sürüm</span></div>
  <div class=""stat""><b>{(released == null ? "—" : $"{upToDate}/{online.Count}")}</b><span>Güncellenen istemci{(failed > 0 ? $" · <span class=\"bad-text\">{failed} hata</span>" : "")}</span></div>
</div>");

            // Güncelleme adımları
            var canRelease = prestaged != null && (released == null || released.PackageId != prestaged.PackageId);
            var openList = mikroOpen.Count == 0
                ? "<span class=\"pill ok\">Hiçbir bilgisayarda Mikro açık değil</span>"
                : $"<span class=\"pill warn\">{mikroOpen.Count} bilgisayarda Mikro açık</span> <span class=\"muted\">{E(string.Join(", ", mikroOpen))}</span>";
            var confirm = mikroOpen.Count == 0
                ? $"{prestaged?.Version} sürümü tüm istemcilere kurulacak. Devam edilsin mi?"
                : $"{mikroOpen.Count} bilgisayarda Mikro hâlâ açık. Kullanıcılar uyarılacak ve {_options.ForceCloseAfterMinutes} dakika sonra Mikro kapatılıp güncellenecek. Devam edilsin mi?";

            sb.Append($@"
<div class=""card"">
  <h2>Güncelleme adımları</h2>
  <ol class=""steps"">
    <li>
      <b>Kullanıcıları uyarın.</b> {openList}
      <form method=""post"" action=""/ui/announce"" class=""row"" style=""margin-top:8px"">
        <input name=""message"" value=""{E(DefaultAnnouncement)}"" style=""flex:1;min-width:260px"">
        <button class=""secondary"" type=""submit"">Mikro kullanıcılarına uyarı gönder</button>
      </form>
    </li>
    <li><b>Mikro sunucusunu güncelleyin.</b> <span class=""muted"">Sunucu setup'ını her zamanki gibi çalıştırın.</span></li>
    <li>
      <b>İstemcileri güncelleyin.</b>
      {(prestaged == null
        ? "<span class=\"muted\">Dağıtılacak yeni sürüm henüz sunucuya inmedi.</span>"
        : canRelease
            ? $@"<form method=""post"" action=""/ui/release"" onsubmit=""return confirm('{EJs(confirm)}')"" style=""margin-top:8px"">
                   <button type=""submit"" class=""big"">İstemcileri Güncelle ({E(prestaged.Version)})</button>
                 </form>"
            : releaseInProgress
                ? "<span class=\"pill warn\">Dağıtım sürüyor</span>"
                : "<span class=\"pill ok\">Tüm istemciler güncel</span>")}
    </li>
  </ol>
</div>");

            // İstemci tablosu
            sb.Append("<div class=\"card\"><h2>İstemciler</h2>");
            if (clients.Count == 0)
            {
                sb.Append("<p class=\"muted\">Henüz bağlanan istemci yok. İstemci ajanı kurulan bilgisayarlar 1 dakika içinde burada görünür.</p>");
            }
            else
            {
                sb.Append("<div class=\"table-wrap\"><table><thead><tr><th>Bilgisayar</th><th>Durum</th><th>Mikro</th><th>Kurulu sürüm</th><th>Hazır sürüm</th><th>Son rapor</th></tr></thead><tbody>");
                foreach (var c in clients)
                {
                    var isOnline = Online(c);
                    var state = isOnline
                        ? $"<span class=\"pill {StateCss(c.State)}\">{StateText(c.State)}</span>"
                        : "<span class=\"pill\">Çevrimdışı</span>";
                    if (c.LastError != null) state += $"<div class=\"err\">{E(c.LastError)}</div>";
                    sb.Append($@"<tr{(isOnline ? "" : " class=\"dim\"")}>
  <td>{E(c.MachineName)}</td><td>{state}</td>
  <td>{(isOnline && c.MikroRunning ? "<span class=\"pill warn\">Açık</span>" : "Kapalı")}</td>
  <td>{E(c.InstalledVersion ?? "—")}</td><td>{E(c.PrestagedVersion ?? "—")}</td>
  <td class=""muted"">{Ago(now - c.ReportedAtUtc)}</td></tr>");
                }
                sb.Append("</tbody></table></div>");
            }
            sb.Append("</div><p class=\"muted small\">Bu sayfa 15 saniyede bir kendini yeniler.</p>");

            return Page("Sunucu", sb.ToString(), refresh: true);
        }

        private static string StateText(ClientState state)
        {
            switch (state)
            {
                case ClientState.UpToDate: return "Güncel";
                case ClientState.Downloading: return "İndiriliyor";
                case ClientState.Ready: return "Hazır";
                case ClientState.WaitingForMikroToClose: return "Mikro'nun kapanması bekleniyor";
                case ClientState.Installing: return "Kuruluyor";
                case ClientState.Failed: return "Hata";
                default: return "Bilinmiyor";
            }
        }

        private static string StateCss(ClientState state)
        {
            switch (state)
            {
                case ClientState.UpToDate: return "ok";
                case ClientState.Failed: return "bad";
                case ClientState.WaitingForMikroToClose:
                case ClientState.Installing: return "warn";
                default: return "";
            }
        }

        private static string Ago(TimeSpan span)
        {
            if (span < TimeSpan.FromMinutes(1)) return "az önce";
            if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} dk önce";
            if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} saat önce";
            return $"{(int)span.TotalDays} gün önce";
        }

        private static void Flash(StringBuilder sb, bool ok, string text) =>
            sb.Append($"<div class=\"flash {(ok ? "ok" : "bad")}\">{E(text)}</div>");

        /// <summary>HTML kaçışı; Türkçe karakterlere dokunmaz, sadece &lt; &gt; &amp; &quot; ' kaçırılır.</summary>
        private static string E(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text!.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>onsubmit="confirm('...')" içine güvenle yazılacak metin.</summary>
        private static string EJs(string text) =>
            E(text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", " "));

        private static string Page(string title, string body, bool refresh) => $@"<!doctype html>
<html lang=""tr""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1"">
{(refresh ? "<meta http-equiv=\"refresh\" content=\"15;url=/ui\">" : "")}
<title>{E(title)} · BiDeploy</title>
<style>
:root {{ --bg:#f5f6f8; --card:#fff; --text:#1d2330; --muted:#6b7280; --line:#e3e6eb; --brand:#1f5fbf;
  --ok:#16794a; --ok-bg:#e5f4ec; --warn:#9a5b00; --warn-bg:#fdf1dc; --bad:#b3261e; --bad-bg:#fbe7e6; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#14171c; --card:#1d2128; --text:#e7e9ee; --muted:#9aa1ad; --line:#2e333c;
  --brand:#6ea2f0; --ok:#62c793; --ok-bg:#173325; --warn:#f0b14d; --warn-bg:#3a2c12; --bad:#f08a84; --bad-bg:#3b1b19; }} }}
* {{ box-sizing:border-box; }}
body {{ margin:0; font:15px/1.5 system-ui,-apple-system,""Segoe UI"",sans-serif; background:var(--bg); color:var(--text); }}
main {{ max-width:1100px; margin:0 auto; padding:24px 16px; }}
h1 {{ font-size:22px; margin:0 0 4px; }} h2 {{ font-size:17px; margin:0 0 12px; }}
.card {{ background:var(--card); border:1px solid var(--line); border-radius:10px; padding:18px; margin-bottom:18px; }}
.card.narrow {{ max-width:380px; margin:60px auto; }}
.row {{ display:flex; gap:10px; flex-wrap:wrap; align-items:center; }}
.head {{ justify-content:space-between; margin-bottom:18px; }}
.stats {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(200px,1fr)); gap:12px; margin-bottom:18px; }}
.stat {{ background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px; }}
.stat b {{ display:block; font-size:22px; }} .stat span {{ color:var(--muted); font-size:13px; }}
.steps {{ margin:0; padding-left:22px; }} .steps li {{ margin-bottom:14px; }}
.table-wrap {{ overflow-x:auto; }}
table {{ width:100%; border-collapse:collapse; }}
th, td {{ text-align:left; padding:9px 10px; border-bottom:1px solid var(--line); vertical-align:top; }}
th {{ font-size:13px; color:var(--muted); font-weight:600; }}
tr.dim td {{ opacity:.55; }}
.muted {{ color:var(--muted); }} .small {{ font-size:13px; }} .bad-text {{ color:var(--bad); }}
.pill {{ display:inline-block; padding:2px 9px; border-radius:999px; font-size:12.5px; background:var(--line); white-space:nowrap; }}
.pill.ok {{ background:var(--ok-bg); color:var(--ok); }} .pill.warn {{ background:var(--warn-bg); color:var(--warn); }}
.pill.bad {{ background:var(--bad-bg); color:var(--bad); }}
.flash {{ padding:10px 14px; border-radius:8px; margin-bottom:16px; }}
.flash.ok {{ background:var(--ok-bg); color:var(--ok); }} .flash.bad {{ background:var(--bad-bg); color:var(--bad); }}
.err {{ color:var(--bad); font-size:13px; max-width:420px; }}
form {{ margin:0; }}
label {{ display:block; font-size:13px; color:var(--muted); margin-bottom:4px; }}
input {{ font:inherit; padding:8px 10px; border:1px solid var(--line); border-radius:7px; background:var(--bg); color:var(--text); width:100%; margin-bottom:12px; }}
.row input {{ width:auto; margin:0; }}
button {{ font:inherit; padding:8px 14px; border-radius:7px; border:1px solid var(--brand); background:var(--brand); color:#fff; cursor:pointer; }}
button.big {{ padding:11px 20px; font-size:16px; font-weight:600; }}
button.secondary {{ background:transparent; color:var(--brand); }}
button.link {{ background:none; border:none; color:var(--brand); padding:0; }}
</style></head><body><main>{body}</main></body></html>";

        private static async Task Html(HttpListenerResponse response, string html)
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            response.ContentType = "text/html; charset=utf-8";
            response.AddHeader("Cache-Control", "no-store");
            response.AddHeader("X-Frame-Options", "DENY");
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        private static void Redirect(HttpListenerResponse response, string location)
        {
            response.StatusCode = 303;
            response.AddHeader("Location", location);
        }

        private static async Task<NameValueCollection> ReadForm(HttpListenerRequest request)
        {
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync().ConfigureAwait(false);
            var result = new NameValueCollection();
            foreach (var pair in body.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var i = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((i < 0 ? pair : pair.Substring(0, i)).Replace('+', ' '));
                var value = i < 0 ? "" : Uri.UnescapeDataString(pair.Substring(i + 1).Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }
    }
}

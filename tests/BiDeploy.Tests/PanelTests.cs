using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiDeploy.Tests;

/// <summary>Web paneli: giriş, yetki sınırları, yöneticinin bayi/lisans işlemleri, bayinin firma ve durum ekranları.</summary>
public sealed class PanelTests : IDisposable
{
    private const string AdminEmail = "admin@biyazilim.com.tr";
    private const string AdminPassword = "Yonetici-Sifre-1";

    private readonly TempDir _temp = new();
    private readonly WebApplicationFactory<Program> _factory;

    public PanelTests()
    {
        var pubPath = _temp.Sub("publisher.pub.json");
        File.WriteAllText(pubPath, PackageSigning.GenerateKey().PublicOnly().ToJson());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("BiDeploy:AdminKey", "test-admin-key");
            b.UseSetting("BiDeploy:PublisherPublicKeyPath", pubPath);
            b.UseSetting("BiDeploy:StorageDirectory", _temp.Sub("storage"));
            b.UseSetting("BiDeploy:InitialAdminEmail", AdminEmail);
            b.UseSetting("BiDeploy:InitialAdminPassword", AdminPassword);
            b.UseSetting("ConnectionStrings:BiDeploy", $"Data Source={_temp.Sub("vps.db")}");
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public async Task Pages_require_login()
    {
        var browser = NewBrowser();
        var response = await browser.GetAsync("/Panel");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Giris", response.Headers.Location!.PathAndQuery);

        Assert.False(await browser.LoginAsync(AdminEmail, "yanlis-sifre"));

        // Giriş, ardından sayfa başlığındaki "Çıkış" formu ile çıkış.
        Assert.True(await browser.LoginAsync(AdminEmail, AdminPassword));
        var logoutForm = Regex.Match(await browser.GetStringAsync("/Yonetim"), "<form[^>]*action=\"/Cikis\".*?</form>", RegexOptions.Singleline).Value;
        Assert.Contains("__RequestVerificationToken", logoutForm);
        var loggedOut = await browser.PostFormAsync("/Yonetim", "/Cikis", new());
        Assert.Contains("Giriş yap", loggedOut);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Yonetim")).StatusCode);
    }

    [Fact]
    public async Task Admin_and_dealer_full_panel_flow()
    {
        // --- Yönetici: bayi oluşturur, havuza lisans ekler ---
        var admin = NewBrowser();
        Assert.True(await admin.LoginAsync(AdminEmail, AdminPassword));
        var created = await admin.PostFormAsync("/Yonetim", "/Yonetim?handler=CreateDealer",
            new() { ["Name"] = "Örnek Bilişim", ["Email"] = "bayi@ornek.com.tr", ["Password"] = "Bayi-Sifre-1" });
        Assert.Contains("Örnek Bilişim oluşturuldu", created);
        Assert.Contains("API anahtarı", created);
        var dealerId = Regex.Match(created, "name=\"dealerId\" value=\"(\\d+)\"").Groups[1].Value;
        var added = await admin.PostFormAsync("/Yonetim", "/Yonetim?handler=AddLicenses", new() { ["dealerId"] = dealerId, ["count"] = "2" });
        Assert.Contains("havuzuna 2 lisans eklendi", added);

        // --- Bayi: yönetim sayfalarına giremez ---
        var dealer = NewBrowser();
        Assert.True(await dealer.LoginAsync("bayi@ornek.com.tr", "Bayi-Sifre-1"));
        Assert.Equal(HttpStatusCode.Redirect, (await dealer.GetAsync("/Yonetim")).StatusCode);

        // --- Bayi: hatalı VKN reddedilir, doğru VKN ile firma + lisans ---
        var invalid = await dealer.PostFormAsync("/Panel/YeniFirma", "/Panel/YeniFirma",
            new() { ["Title"] = "Hatalı", ["Vkn"] = "1111111111", ["AssignLicense"] = "true" });
        Assert.Contains("geçersiz", invalid);

        var firma = await dealer.PostFormAsync("/Panel/YeniFirma", "/Panel/YeniFirma",
            new() { ["Title"] = "ABC Ltd", ["Vkn"] = "1111111114", ["AssignLicense"] = "true" });
        Assert.Contains("lisans atandı", firma);
        var code = Regex.Match(firma, "<code class=\"big\">([A-Z0-9-]+)</code>").Groups[1].Value;
        Assert.Matches("^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$", code);
        Assert.Contains("Kurulum bekleniyor", firma);
        var firmaPath = Regex.Match(firma, "action=\"(/Panel/Firma/\\d+)").Groups[1].Value;
        if (firmaPath == "") firmaPath = "/Panel/Firma/1";

        // --- Sunucu ajanı etkinleşir ve rapor gönderir (API üzerinden) ---
        var agent = _factory.CreateClient();
        var activation = await (await agent.PostAsJsonAsync("/api/agent/activate",
            new ActivationRequest { ActivationCode = code, Vkn = "1111111114", MachineId = "m1", MachineName = "MIKROSUNUCU" }))
            .Content.ReadFromJsonAsync<ActivationResponse>(JsonDefaults.Options);
        using var report = new HttpRequestMessage(HttpMethod.Post, "/api/agent/report")
        {
            Content = JsonContent.Create(new ServerReport
            {
                PrestagedVersion = "17.7.4.46277",
                ReleasedVersion = "17.7.4.46277",
                Clients =
                {
                    new ClientStatus { MachineName = "MUHASEBE-PC", State = ClientState.UpToDate, InstalledVersion = "17.7.4.46277", ReportedAtUtc = DateTime.UtcNow },
                    new ClientStatus { MachineName = "DEPO-PC", State = ClientState.Failed, LastError = "Kurulum çıkış kodu 5.", ReportedAtUtc = DateTime.UtcNow },
                    new ClientStatus { MachineName = "SATIS-PC", State = ClientState.WaitingForMikroToClose, MikroRunning = true, ReportedAtUtc = DateTime.UtcNow },
                },
            }, options: JsonDefaults.Options),
        };
        report.Headers.Authorization = new("Bearer", activation!.DeviceToken);
        (await agent.SendAsync(report)).EnsureSuccessStatusCode();

        // --- Bayi panelinde durum ---
        var list = await dealer.GetStringAsync("/Panel");
        Assert.Contains("ABC Ltd", list);
        Assert.Contains("Çevrimiçi", list);
        Assert.Contains("1/3 güncel", list);
        Assert.Contains("1 hata", list);
        Assert.Contains("1 Mikro açık", list);

        var detail = await dealer.GetStringAsync(firmaPath);
        Assert.Contains("MIKROSUNUCU", detail);
        Assert.Contains("DEPO-PC", detail);
        Assert.Contains("Kurulum çıkış kodu 5.", detail);
        Assert.Contains("kapanması bekleniyor", detail);
        Assert.Contains("365 gün", detail);

        // --- Yenileme ve taşıma formları ---
        var renewed = await dealer.PostFormAsync(firmaPath, firmaPath + "?handler=Renew", new());
        Assert.Contains("1 yıl uzatıldı", renewed);
        Assert.Contains("730 gün", renewed);
        var transferred = await dealer.PostFormAsync(firmaPath, firmaPath + "?handler=Transfer", new());
        Assert.Contains("sunucudan çözüldü", transferred);
        Assert.Contains("Kurulum bekleniyor", transferred);

        // --- Başka bayi bu firmayı göremez ---
        var other = NewBrowser();
        Assert.True(await other.LoginAsync(AdminEmail, AdminPassword));
        await other.PostFormAsync("/Yonetim", "/Yonetim?handler=CreateDealer",
            new() { ["Name"] = "Rakip Bilişim", ["Email"] = "rakip@ornek.com.tr", ["Password"] = "Rakip-Sifre-1" });
        var rival = NewBrowser();
        Assert.True(await rival.LoginAsync("rakip@ornek.com.tr", "Rakip-Sifre-1"));
        Assert.Equal(HttpStatusCode.NotFound, (await rival.GetAsync(firmaPath)).StatusCode);
        Assert.DoesNotContain("ABC Ltd", await rival.GetStringAsync("/Panel"));
    }

    private Browser NewBrowser() => new(_factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true }));

    /// <summary>Çerezleri tutan, antiforgery belirteciyle form gönderen ve yönlendirmeleri takip eden basit tarayıcı.</summary>
    private sealed class Browser(HttpClient http)
    {
        public Task<HttpResponseMessage> GetAsync(string path) => http.GetAsync(path);

        public async Task<string> GetStringAsync(string path)
        {
            var response = await http.GetAsync(path);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        public async Task<bool> LoginAsync(string email, string password)
        {
            var html = await PostFormAsync("/Giris", "/Giris", new() { ["Email"] = email, ["Password"] = password });
            return !html.Contains("E-posta veya şifre hatalı");
        }

        /// <summary>formPage'i açıp belirteci alır, postPath'e gönderir; yönlendirme varsa hedef sayfanın HTML'ini döner.</summary>
        public async Task<string> PostFormAsync(string formPage, string postPath, Dictionary<string, string> fields)
        {
            var page = await GetStringAsync(formPage);
            var token = Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(token);
            fields["__RequestVerificationToken"] = token;
            var response = await http.PostAsync(postPath, new FormUrlEncodedContent(fields));
            for (var i = 0; i < 3 && response.StatusCode == HttpStatusCode.Redirect; i++)
                response = await http.GetAsync(response.Headers.Location!);
            Assert.True(response.IsSuccessStatusCode, $"{postPath} → {(int)response.StatusCode}");
            return await response.Content.ReadAsStringAsync();
        }
    }
}

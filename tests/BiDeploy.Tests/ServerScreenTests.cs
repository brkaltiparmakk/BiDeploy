using System.Net;
using System.Text;
using BiDeploy.Agent.Core;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;
using Xunit;

namespace BiDeploy.Tests;

/// <summary>Mikro sunucusundaki bayi ekranı (http://localhost:port/ui).</summary>
public sealed class ServerScreenTests : IDisposable
{
    private const string LanKey = "firma-lan-anahtari-1234";
    private const string AdminPassword = "sunucu-sifresi-1";
    private const string Version = "17.7.4.46277";

    private readonly TempDir _temp = new();
    private readonly PackageSigning.KeyFile _key = PackageSigning.GenerateKey();
    private readonly FakeClock _clock = new();
    private readonly AgentOptions _options;
    private readonly AgentState _state = new() { DeviceToken = "token", CompanyTitle = "ABC Gıda Ltd.", LicenseExpiresAtUtc = new DateTime(2027, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
    private readonly ServerAgent _server;
    private readonly LanServer _lan;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _lanTask;
    private readonly string _baseUrl;

    public ServerScreenTests()
    {
        var port = Ports.Free();
        _baseUrl = $"http://localhost:{port}/";
        _options = new AgentOptions
        {
            Role = AgentRole.Server, VpsUrl = "http://127.0.0.1:1/", LanKey = LanKey, AdminPassword = AdminPassword,
            DataDirectory = _temp.Sub("server"), LanListenPrefix = _baseUrl,
        };
        _options.Validate();
        _server = new ServerAgent(_options, _state, new VpsClient(new HttpClient(), _options.VpsUrl),
            new PackageCache(_options.PackagesDirectory, _key.PublicOnly()), _clock, new TestLog());
        _lan = new LanServer(_baseUrl, LanKey, _server, new TestLog(), new LocalUi(_server, _options));
        _lan.Start();
        _lanTask = _lan.RunAsync(_cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _lanTask.Wait(5000);
        _lan.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void Admin_password_must_differ_from_lan_key()
    {
        var options = new AgentOptions { Role = AgentRole.Server, VpsUrl = "https://x", LanKey = LanKey, AdminPassword = LanKey };
        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public async Task Screen_requires_admin_password()
    {
        using var browser = NewBrowser();
        var login = await browser.GetStringAsync("ui");
        Assert.Contains("Yönetici şifresi", login);
        Assert.DoesNotContain("İstemcileri Güncelle", login);

        Assert.Contains("Şifre hatalı", await Post(browser, "ui/login", ("password", LanKey)));

        // Giriş yapmadan güncelleme başlatılamaz.
        SeedPrestagedPackage();
        await Post(browser, "ui/release");
        Assert.Null(_state.ReleasedPackageId);

        // LAN uçları hâlâ firma anahtarı ister.
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("lan/v1/target")).StatusCode);
    }

    [Fact]
    public async Task Update_night_from_the_server_screen()
    {
        SeedPrestagedPackage();
        _server.AcceptClientReport(new ClientStatus { MachineName = "MUHASEBE-1", State = ClientState.Ready, PrestagedVersion = Version, InstalledVersion = "17.7.3.46010" });
        _server.AcceptClientReport(new ClientStatus { MachineName = "SATIS-PC", State = ClientState.Ready, PrestagedVersion = Version, MikroRunning = true });

        using var browser = NewBrowser();
        var dashboard = await Post(browser, "ui/login", ("password", AdminPassword));
        Assert.Contains("ABC Gıda Ltd.", dashboard);
        Assert.Contains("2/2", dashboard);                          // yeni sürümü hazır bekleyen istemci
        Assert.Contains("1 bilgisayarda Mikro açık", dashboard);
        Assert.Contains("SATIS-PC", dashboard);
        Assert.Contains($"İstemcileri Güncelle ({Version})", dashboard);

        // 1) Kullanıcıları uyar: duyuru istemcilere yerel ağdan gider, Mikro'su açık olanda bir kez gösterilir.
        var announced = await Post(browser, "ui/announce", ("message", "Sunucu 22:00'de güncellenecek, Mikro'dan çıkın."));
        Assert.Contains("Uyarı gönderildi", announced);

        var notifier = new FakeNotifier();
        var processes = new FakeProcesses { Running = true };
        var client = NewClient(processes, notifier);
        await client.RunCycleAsync(CancellationToken.None);
        await client.RunCycleAsync(CancellationToken.None);
        Assert.Equal(new[] { "Sunucu 22:00'de güncellenecek, Mikro'dan çıkın." }, notifier.Messages);

        // 3) İstemcileri Güncelle.
        var released = await Post(browser, "ui/release");
        Assert.Contains($"{Version} istemcilere dağıtılıyor", released);
        Assert.Equal($"Fly-x64-{Version}", _state.ReleasedPackageId);
        Assert.Contains("Dağıtım sürüyor", released);
        Assert.Contains("content=\"15;url=/ui\"", released); // otomatik yenileme mesajı tekrar göstermez
        Assert.DoesNotContain("İstemcileri Güncelle (", released);

        // Raporu gelmeyen istemci çevrimdışı gösterilir.
        _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
        Assert.Contains("Çevrimdışı", await browser.GetStringAsync("ui"));

        // Çıkıştan sonra ekran tekrar şifre ister.
        Assert.Contains("Yönetici şifresi", await Post(browser, "ui/logout"));
    }

    [Fact]
    public async Task Check_now_wakes_the_server_loop()
    {
        using var browser = NewBrowser();
        await Post(browser, "ui/login", ("password", AdminPassword));
        var wait = _server.WaitForNextCycleAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.False(wait.IsCompleted);
        Assert.Contains("kontrolü başlatıldı", await Post(browser, "ui/check"));
        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(5000)));
    }

    private ClientAgent NewClient(FakeProcesses processes, FakeNotifier notifier)
    {
        var options = new AgentOptions { Role = AgentRole.Client, ServerUrl = _baseUrl, LanKey = LanKey, DataDirectory = _temp.Sub("client") };
        return new ClientAgent(options, new AgentState(), new HttpClient(), new PackageCache(options.PackagesDirectory, _key.PublicOnly()),
            processes, new FakeInstaller(), notifier, _clock, new TestLog(), "SATIS-PC");
    }

    /// <summary>Sunucu ajanının VPS'ten paketi indirmiş olduğu durumu doğrudan önbelleğe yazarak kurar.</summary>
    private void SeedPrestagedPackage()
    {
        var setup = Encoding.UTF8.GetBytes("sahte setup");
        var manifest = new PackageManifest
        {
            Product = "Fly", Architecture = "x64", Version = Version, FileName = "Fly_v17xx_Client_Setupx064.exe",
            FileSize = setup.Length, Sha256 = Hashing.Sha256Hex(setup), InstallerArguments = "/VERYSILENT", MainExecutable = "MikroFly.exe",
        };
        var bytes = manifest.ToJsonBytes();
        var dir = Path.Combine(_options.PackagesDirectory, manifest.PackageId);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, manifest.FileName), setup);
        File.WriteAllBytes(Path.Combine(dir, "manifest.json"), bytes);
        File.WriteAllText(Path.Combine(dir, "manifest.sig"), PackageSigning.Sign(bytes, _key));
        File.WriteAllText(Path.Combine(dir, ".ready"), "");
        _state.PrestagedPackageId = manifest.PackageId;
    }

    private HttpClient NewBrowser() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri(_baseUrl) };

    private static async Task<string> Post(HttpClient browser, string path, params (string Key, string Value)[] fields)
    {
        var response = await browser.PostAsync(path, new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}

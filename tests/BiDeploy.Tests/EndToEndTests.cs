using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BiDeploy.Agent.Core;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;
using BiDeploy.Publisher;
using BiDeploy.Server.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BiDeploy.Tests;

/// <summary>
/// Güncelleme günü akışının tamamı: yayın → sunucu ajanı önceden indirir → istemci önceden indirir →
/// bayi sunucuyu günceller ve "İstemcileri Güncelle" der → açık Mikro uyarılır ve kapatılır → sessiz kurulum
/// (hata olursa bekleyip yeniden dener) → rapor bayi panelinde.
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private const string AdminKey = "test-admin-key";
    private const string LanKey = "firma-lan-anahtari-1234";
    private const string Vkn = "1111111114";
    private const string NewVersion = "17.7.4.46277";

    private readonly TempDir _temp = new();
    private readonly PackageSigning.KeyFile _key = PackageSigning.GenerateKey();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _vpsHttp;

    public EndToEndTests()
    {
        var pubPath = _temp.Sub("publisher.pub.json");
        File.WriteAllText(pubPath, _key.PublicOnly().ToJson());
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("BiDeploy:AdminKey", AdminKey);
            b.UseSetting("BiDeploy:PublisherPublicKeyPath", pubPath);
            b.UseSetting("BiDeploy:StorageDirectory", _temp.Sub("storage"));
            b.UseSetting("ConnectionStrings:BiDeploy", $"Data Source={_temp.Sub("vps.db")}");
        });
        _vpsHttp = _factory.CreateClient();
    }

    public void Dispose()
    {
        _vpsHttp.Dispose();
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    [Fact]
    public async Task Full_update_day_flow()
    {
        // --- BiYazılım: bayi oluşturur, havuza lisans yükler ---
        var dealerKey = await CreateDealerWithLicenses(1);
        var dealer = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = _factory.Server.BaseAddress };
        dealer.DefaultRequestHeaders.Add("X-Api-Key", dealerKey);

        // --- Bayi: müşteriyi VKN ile ekler, lisans atar ---
        Assert.Equal(HttpStatusCode.BadRequest,
            (await dealer.PostAsJsonAsync("/api/dealer/companies", new { vkn = "1111111111", title = "Hatalı" })).StatusCode);
        var company = await (await dealer.PostAsJsonAsync("/api/dealer/companies", new { vkn = Vkn, title = "ABC Ltd" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var companyId = company.GetProperty("id").GetInt32();
        var licensed = await (await dealer.PostAsync($"/api/dealer/companies/{companyId}/license", null))
            .Content.ReadFromJsonAsync<DealerEndpoints.CompanyView>(JsonDefaults.Options);
        var activationCode = licensed!.ActivationCode!;
        var me = await dealer.GetFromJsonAsync<JsonElement>("/api/dealer/me");
        Assert.Equal(0, me.GetProperty("availableLicenses").GetInt32());

        // --- BiYazılım: yeni Mikro sürümünü imzalayıp yayınlar ---
        var setupPath = _temp.Sub("Fly_v17xx_Client_Setupx064.exe");
        File.WriteAllBytes(setupPath, RandomBytes(256 * 1024));
        var keyPath = _temp.Sub("publisher.key.json");
        File.WriteAllText(keyPath, _key.ToJson());
        var publishOutput = new StringWriter();
        var exit = await PublisherCli.RunAsync(
            ["publish", setupPath, "--key", keyPath, "--server", _factory.Server.BaseAddress.ToString(), "--admin-key", AdminKey, "--version", NewVersion],
            publishOutput, publishOutput, _factory.Server.CreateHandler());
        Assert.True(exit == 0, publishOutput.ToString());

        // --- Sunucu ajanı: etkinleşir, yeni sürümü önceden indirir ---
        var serverOptions = new AgentOptions
        {
            Role = AgentRole.Server, VpsUrl = _factory.Server.BaseAddress.ToString(), LanKey = LanKey,
            DataDirectory = _temp.Sub("server-agent"),
        };
        var serverState = new AgentState();
        var serverLog = new TestLog();
        var clock = new FakeClock();
        var server = new ServerAgent(serverOptions, serverState, new VpsClient(_vpsHttp, serverOptions.VpsUrl),
            new PackageCache(serverOptions.PackagesDirectory, _key.PublicOnly()), clock, serverLog);

        await server.ActivateAsync(activationCode, Vkn, "machine-1", "MIKROSUNUCU", CancellationToken.None);
        // Paket henüz indirilmeden "İstemcileri Güncelle" reddedilir.
        Assert.Throws<InvalidOperationException>(() => server.ReleaseForClients());
        await server.RunCycleAsync(CancellationToken.None);
        Assert.Equal($"Fly-x64-{NewVersion}", serverState.PrestagedPackageId);
        Assert.Null(serverState.ReleasedPackageId);

        // --- Yerel ağ sunucusu ve istemci ajanı ---
        var port = Ports.Free();
        using var lan = new LanServer($"http://localhost:{port}/", LanKey, server, serverLog);
        lan.Start();
        using var lanCts = new CancellationTokenSource();
        var lanTask = lan.RunAsync(lanCts.Token);

        var processes = new FakeProcesses();
        var installer = new FakeInstaller { ExitCode = 1 };
        var notifier = new FakeNotifier();
        var clientOptions = new AgentOptions
        {
            Role = AgentRole.Client, ServerUrl = $"http://localhost:{port}/", LanKey = LanKey,
            DataDirectory = _temp.Sub("client-agent"), ForceCloseAfterMinutes = 10,
        };
        using var clientHttp = new HttpClient();
        var client = new ClientAgent(clientOptions, new AgentState(), clientHttp,
            new PackageCache(clientOptions.PackagesDirectory, _key.PublicOnly()),
            processes, installer, notifier, clock, new TestLog(), "MUHASEBE-PC");

        // 1) İstemci paketi önceden indirir ama kurmaz.
        await client.RunCycleAsync(CancellationToken.None);
        Assert.Equal(ClientState.Ready, client.LastStatus.State);
        Assert.Equal(NewVersion, client.LastStatus.PrestagedVersion);
        Assert.Empty(installer.Runs);

        // 2) Bayi sunucuyu elle günceller ve "İstemcileri Güncelle"ye basar. Kullanıcıda Mikro açık.
        var released = server.ReleaseForClients();
        Assert.Equal(NewVersion, released.Version);
        processes.Running = true;

        await client.RunCycleAsync(CancellationToken.None);
        Assert.Equal(ClientState.WaitingForMikroToClose, client.LastStatus.State);
        Assert.Single(notifier.Messages);
        Assert.Empty(installer.Runs);

        // 3) 5 dakika sonra hâlâ açık: tekrar uyarılmaz, beklenir.
        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        await client.RunCycleAsync(CancellationToken.None);
        Assert.Equal(ClientState.WaitingForMikroToClose, client.LastStatus.State);
        Assert.Single(notifier.Messages);

        // 4) Süre doldu: Mikro kapatılır, imzalı setup sessiz parametrelerle çalıştırılır ama hata verir.
        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        await client.RunCycleAsync(CancellationToken.None);
        Assert.Equal(ClientState.Failed, client.LastStatus.State);
        Assert.Contains("çıkış kodu 1", client.LastStatus.LastError);
        Assert.Equal(new[] { "MikroFly" }, processes.Killed);
        var run = Assert.Single(installer.Runs);
        Assert.Equal(PublisherCli.DefaultInstallerArguments, run.Args);

        // 5) Hatadan sonra her dakika yeniden denenmez; 15 dakika beklenir.
        clock.UtcNow = clock.UtcNow.AddMinutes(5);
        await client.RunCycleAsync(CancellationToken.None);
        Assert.Equal(ClientState.Failed, client.LastStatus.State);
        Assert.Single(installer.Runs);

        // 6) Süre dolunca yeniden denenir ve bu sefer başarılı olur.
        installer.ExitCode = 0;
        clock.UtcNow = clock.UtcNow.AddMinutes(11);
        await client.RunCycleAsync(CancellationToken.None);
        Assert.Equal(ClientState.UpToDate, client.LastStatus.State);
        Assert.Equal(NewVersion, client.LastStatus.InstalledVersion);
        Assert.Equal(2, installer.Runs.Count);

        // 7) Aynı paket ikinci kez kurulmaz.
        await client.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, installer.Runs.Count);

        // --- Sunucu ajanı VPS'e rapor eder, bayi panelinde görünür ---
        await server.RunCycleAsync(CancellationToken.None);
        var companies = await dealer.GetFromJsonAsync<List<DealerEndpoints.CompanyView>>("/api/dealer/companies", JsonDefaults.Options);
        var view = Assert.Single(companies!);
        Assert.True(view.Activated);
        Assert.Equal(NewVersion, view.LastReport!.ReleasedVersion);
        var clientView = Assert.Single(view.LastReport.Clients);
        Assert.Equal("MUHASEBE-PC", clientView.MachineName);
        Assert.Equal(ClientState.UpToDate, clientView.State);
        Assert.Equal(NewVersion, clientView.InstalledVersion);

        lanCts.Cancel();
        await lanTask;
    }

    [Fact]
    public async Task License_is_bound_to_first_server_until_dealer_transfers_it()
    {
        var dealerKey = await CreateDealerWithLicenses(1);
        var dealer = new HttpClient(_factory.Server.CreateHandler()) { BaseAddress = _factory.Server.BaseAddress };
        dealer.DefaultRequestHeaders.Add("X-Api-Key", dealerKey);
        var company = await (await dealer.PostAsJsonAsync("/api/dealer/companies", new { vkn = Vkn, title = "ABC Ltd" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = company.GetProperty("id").GetInt32();
        var view = await (await dealer.PostAsync($"/api/dealer/companies/{id}/license", null)).Content.ReadFromJsonAsync<DealerEndpoints.CompanyView>(JsonDefaults.Options);

        async Task<HttpStatusCode> Activate(string machine, string vkn = Vkn) =>
            (await _vpsHttp.PostAsJsonAsync("/api/agent/activate",
                new ActivationRequest { ActivationCode = view!.ActivationCode!, Vkn = vkn, MachineId = machine, MachineName = machine })).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Activate("m1", vkn: "12345678950")); // yanlış VKN
        Assert.Equal(HttpStatusCode.OK, await Activate("m1"));
        Assert.Equal(HttpStatusCode.OK, await Activate("m1"));        // aynı sunucuda yeniden kurulum
        Assert.Equal(HttpStatusCode.Conflict, await Activate("m2"));  // başka sunucu
        Assert.Equal(HttpStatusCode.OK, (await dealer.PostAsync($"/api/dealer/companies/{id}/license/transfer", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Activate("m2"));

        // Havuz boşken ikinci firmaya lisans atanamaz.
        var second = await (await dealer.PostAsJsonAsync("/api/dealer/companies", new { vkn = "12345678950", title = "Şahıs Firması" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.BadRequest, (await dealer.PostAsync($"/api/dealer/companies/{second.GetProperty("id").GetInt32()}/license", null)).StatusCode);
    }

    [Fact]
    public async Task Vps_rejects_package_signed_with_unknown_key()
    {
        var setupPath = _temp.Sub("Fly_v17xx_Client_Setupx064.exe");
        File.WriteAllBytes(setupPath, RandomBytes(1024));
        var foreignKeyPath = _temp.Sub("foreign.key.json");
        File.WriteAllText(foreignKeyPath, PackageSigning.GenerateKey().ToJson());
        var output = new StringWriter();

        var exit = await PublisherCli.RunAsync(
            ["publish", setupPath, "--key", foreignKeyPath, "--server", _factory.Server.BaseAddress.ToString(), "--admin-key", AdminKey, "--version", NewVersion],
            output, output, _factory.Server.CreateHandler());

        Assert.NotEqual(0, exit);
        Assert.Contains("imza", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Admin_endpoints_require_admin_key()
    {
        var response = await _vpsHttp.PostAsJsonAsync("/api/admin/dealers", new { name = "X" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<string> CreateDealerWithLicenses(int count)
    {
        using var admin = new HttpRequestMessage(HttpMethod.Post, "/api/admin/dealers") { Content = JsonContent.Create(new { name = "Örnek Bilişim" }) };
        admin.Headers.Add("X-Admin-Key", AdminKey);
        var created = await (await _vpsHttp.SendAsync(admin)).Content.ReadFromJsonAsync<JsonElement>();

        using var credit = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/dealers/{created.GetProperty("id").GetInt32()}/licenses")
            { Content = JsonContent.Create(new { count }) };
        credit.Headers.Add("X-Admin-Key", AdminKey);
        (await _vpsHttp.SendAsync(credit)).EnsureSuccessStatusCode();
        return created.GetProperty("apiKey").GetString()!;
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }
}

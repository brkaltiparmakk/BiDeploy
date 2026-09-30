using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BiDeploy.Agent.Core;
using BiDeploy.Core;

namespace BiDeploy.Agent
{
    /// <summary>Ayarları okur, rolleri kurar ve döngüleri çalıştırır. Servis ve konsol modu aynı kodu kullanır.</summary>
    internal sealed class AgentHost
    {
        public AgentOptions Options { get; }
        public IAgentLog Log { get; }
        public ServerAgent? Server { get; }
        public ClientAgent? Client { get; }

        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromHours(1) };

        public AgentHost(bool console)
        {
            Options = AgentOptions.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent.json"));
            Log = new FileAgentLog(Options.LogsDirectory, console);
            var trustedKey = LoadTrustedKey();
            var state = AgentState.Load(Options.StateFile); // İki rol aynı durum nesnesini paylaşır.
            var cache = new PackageCache(Options.PackagesDirectory, trustedKey);
            var mikro = new WindowsMikroInstallation(Options.MikroExePath, Options.Product);
            var clock = new SystemClock();

            if (Options.Role.HasFlag(AgentRole.Server))
                Server = new ServerAgent(Options, state, new VpsClient(_http, Options.VpsUrl), cache, mikro, clock, Log);

            if (Options.Role.HasFlag(AgentRole.Client))
            {
                if (Options.Role == AgentRole.Both && string.IsNullOrWhiteSpace(Options.ServerUrl))
                    Options.ServerUrl = "http://localhost:" + new Uri(Options.LanListenPrefix.Replace("+", "localhost")).Port + "/";
                Client = new ClientAgent(Options, state, _http, cache, mikro, new WindowsProcessControl(),
                    new InnoSetupRunner(), new MsgExeNotifier(Log), clock, Log, Environment.MachineName);
            }
        }

        internal static PackageSigning.KeyFile LoadTrustedKey()
        {
            using var stream = typeof(AgentHost).Assembly.GetManifestResourceStream("BiDeploy.trusted-publisher.pub.json")
                ?? throw new InvalidOperationException("Gömülü yayıncı anahtarı bulunamadı.");
            using var reader = new StreamReader(stream);
            var key = PackageSigning.KeyFile.FromJson(reader.ReadToEnd()).PublicOnly();
            if (string.IsNullOrEmpty(key.Modulus))
                throw new InvalidOperationException("Ajana yayıncı açık anahtarı gömülmemiş (trusted-publisher.pub.json boş). Derlemeden önce keygen çıktısını kopyalayın.");
            return key;
        }

        public async Task RunAsync(CancellationToken ct)
        {
            Log.Info($"BiDeploy ajanı başladı. Rol: {Options.Role}");
            var tasks = new System.Collections.Generic.List<Task>();

            if (Server != null)
            {
                var lan = new LanServer(Options.LanListenPrefix, Options.LanKey, Server, Log);
                lan.Start();
                tasks.Add(lan.RunAsync(ct));
                tasks.Add(Loop("sunucu", Server.RunCycleAsync, TimeSpan.FromSeconds(Options.ServerPollSeconds), ct));
            }
            if (Client != null)
                tasks.Add(Loop("istemci", Client.RunCycleAsync, TimeSpan.FromSeconds(Options.ClientPollSeconds), ct));

            await Task.WhenAll(tasks).ConfigureAwait(false);
            Log.Info("BiDeploy ajanı durdu.");
        }

        private async Task Loop(string name, Func<CancellationToken, Task> cycle, TimeSpan interval, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await cycle(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error($"{name} döngüsünde beklenmeyen hata", ex);
                }

                try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}

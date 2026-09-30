using System;
using System.Net.Http;
using System.ServiceProcess;
using System.Threading;
using BiDeploy.Agent.Core;

namespace BiDeploy.Agent
{
    internal static class Program
    {
        private const string Usage = @"BiDeploy Ajanı

  BiDeploy.Agent.exe run                      Konsolda çalıştır (test için)
  BiDeploy.Agent.exe activate <KOD> <VKN>     Sunucu ajanını lisansla etkinleştir
  BiDeploy.Agent.exe release                  İstemcileri Güncelle (önceden indirilen sürümü kurdur)
  BiDeploy.Agent.exe status                   Sunucu ve istemci durumunu göster
  BiDeploy.Agent.exe ui                       Sunucu ekranını tarayıcıda aç (http://localhost:8765/ui)

Servis olarak kurulum (yönetici komut isteminde):
  sc create BiDeployAgent binPath= ""<yol>\BiDeploy.Agent.exe service"" start= auto
  sc start BiDeployAgent";

        private static int Main(string[] args)
        {
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            try
            {
                switch (command)
                {
                    case "service":
                        ServiceBase.Run(new AgentService());
                        return 0;
                    case "run":
                        return RunConsole();
                    case "activate":
                        return Activate(args);
                    case "release":
                        return CallLocal(HttpMethod.Post, "local/v1/release");
                    case "status":
                        return CallLocal(HttpMethod.Get, "local/v1/status");
                    case "ui":
                        return OpenUi();
                    default:
                        Console.WriteLine(Usage);
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Hata: " + ex.Message);
                return 2;
            }
        }

        private static int RunConsole()
        {
            var host = new AgentHost(console: true);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            host.RunAsync(cts.Token).GetAwaiter().GetResult();
            return 0;
        }

        private static int Activate(string[] args)
        {
            if (args.Length != 3) throw new ArgumentException("Kullanım: activate <KOD> <VKN>");
            var host = new AgentHost(console: true);
            if (host.Server == null) throw new InvalidOperationException("Etkinleştirme sadece sunucu rolünde yapılır.");
            host.Server.ActivateAsync(args[1], args[2], MachineIdentity.GetMachineId(), Environment.MachineName, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("Etkinleştirildi. Servis çalışıyorsa yeniden başlatın: sc stop BiDeployAgent && sc start BiDeployAgent");
            return 0;
        }

        /// <summary>Sunucu ekranını varsayılan tarayıcıda açar (masaüstü kısayolu bu komutu çalıştırır).</summary>
        private static int OpenUi()
        {
            var options = AgentOptions.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent.json"));
            var port = new Uri(options.LanListenPrefix.Replace("+", "localhost").Replace("*", "localhost")).Port;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"http://localhost:{port}/ui") { UseShellExecute = true });
            return 0;
        }

        /// <summary>Çalışan servise aynı makineden komut gönderir (servisin durumunu bozmadan).</summary>
        private static int CallLocal(HttpMethod method, string path)
        {
            var options = AgentOptions.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agent.json"));
            var port = new Uri(options.LanListenPrefix.Replace("+", "localhost").Replace("*", "localhost")).Port;
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(method, $"http://localhost:{port}/{path}");
            request.Headers.Add(LanServer.KeyHeader, options.LanKey);
            using var response = http.SendAsync(request).GetAwaiter().GetResult();
            Console.WriteLine(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return response.IsSuccessStatusCode ? 0 : 3;
        }
    }
}

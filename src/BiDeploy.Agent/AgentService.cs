using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace BiDeploy.Agent
{
    internal sealed class AgentService : ServiceBase
    {
        public const string Name = "BiDeployAgent";

        private CancellationTokenSource? _cts;
        private Task? _run;

        public AgentService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
        }

        protected override void OnStart(string[] args)
        {
            _cts = new CancellationTokenSource();
            var host = new AgentHost(console: false);
            _run = Task.Run(() => host.RunAsync(_cts.Token));
        }

        protected override void OnStop() => StopHost();

        protected override void OnShutdown() => StopHost();

        private void StopHost()
        {
            _cts?.Cancel();
            try { _run?.Wait(30000); } catch (System.AggregateException) { }
        }
    }
}

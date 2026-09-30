using System.Net;
using System.Net.Sockets;
using BiDeploy.Agent.Core;

namespace BiDeploy.Tests;

internal sealed class FakeMikro : IMikroInstallation
{
    public string? Version { get; set; }
    public string? GetInstalledVersion(string mainExecutable) => Version;
}

internal sealed class FakeProcesses : IProcessControl
{
    public bool Running { get; set; }
    public List<string> Killed { get; } = new();
    public bool IsRunning(string processName) => Running;
    public void Kill(string processName)
    {
        Killed.Add(processName);
        Running = false;
    }
}

/// <summary>Kurulumu taklit eder: başarılıysa kurulu sürümü setup'ın sürümüne çeker.</summary>
internal sealed class FakeInstaller(FakeMikro mikro) : IInstallerRunner
{
    public List<(string Setup, string Args)> Runs { get; } = new();
    public int ExitCode { get; set; }
    public string? VersionAfterInstall { get; set; }

    public InstallResult Run(string setupPath, string arguments, string logPath, TimeSpan timeout)
    {
        Runs.Add((setupPath, arguments));
        if (ExitCode == 0) mikro.Version = VersionAfterInstall;
        return new InstallResult { ExitCode = ExitCode, LogTail = ExitCode == 0 ? null : "Setup hata verdi" };
    }
}

internal sealed class FakeNotifier : IUserNotifier
{
    public List<string> Messages { get; } = new();
    public void NotifyAll(string message, TimeSpan displayFor) => Messages.Add(message);
}

internal sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 10, 1, 22, 0, 0, DateTimeKind.Utc);
}

internal sealed class TestLog : IAgentLog
{
    public List<string> Lines { get; } = new();
    public void Info(string message) => Lines.Add("INFO " + message);
    public void Error(string message, Exception? ex = null) => Lines.Add("ERROR " + message + (ex == null ? "" : ": " + ex.Message));
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("bideploy-test-").FullName;
    public string Sub(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal static class Ports
{
    public static int Free()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

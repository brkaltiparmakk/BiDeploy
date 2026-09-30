using System;

namespace BiDeploy.Agent.Core
{
    /// <summary>Kurulu Mikro sürümünü okur (Windows'ta registry + exe FileVersion).</summary>
    public interface IMikroInstallation
    {
        /// <returns>Kurulu değilse null.</returns>
        string? GetInstalledVersion(string mainExecutable);
    }

    public interface IProcessControl
    {
        bool IsRunning(string processName);
        void Kill(string processName);
    }

    public sealed class InstallResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string? LogTail { get; set; }
        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

    public interface IInstallerRunner
    {
        InstallResult Run(string setupPath, string arguments, string logPath, TimeSpan timeout);
    }

    /// <summary>Oturum açmış kullanıcılara mesaj gösterir (terminal server'da tüm oturumlar).</summary>
    public interface IUserNotifier
    {
        void NotifyAll(string message, TimeSpan displayFor);
    }

    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    public interface IAgentLog
    {
        void Info(string message);
        void Error(string message, Exception? ex = null);
    }
}

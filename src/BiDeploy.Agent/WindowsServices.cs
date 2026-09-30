using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BiDeploy.Agent.Core;
using BiDeploy.Core;
using Microsoft.Win32;

namespace BiDeploy.Agent
{
    internal sealed class WindowsProcessControl : IProcessControl
    {
        public bool IsRunning(string processName)
        {
            var processes = Process.GetProcessesByName(processName);
            foreach (var p in processes) p.Dispose();
            return processes.Length > 0;
        }

        public void Kill(string processName)
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                using (p)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(30000);
                    }
                    catch (InvalidOperationException) { /* bu arada kapanmış */ }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }
    }

    /// <summary>Inno Setup kurulumunu sessiz çalıştırır; /LOG ile kurulum logu alınır ve hata durumunda son satırları raporlanır.</summary>
    internal sealed class InnoSetupRunner : IInstallerRunner
    {
        public InstallResult Run(string setupPath, string arguments, string logPath, TimeSpan timeout)
        {
            var info = new ProcessStartInfo(setupPath, $"{arguments} /LOG=\"{logPath}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(setupPath),
            };
            using var process = Process.Start(info)!;
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return new InstallResult { TimedOut = true, LogTail = Tail(logPath) };
            }
            return new InstallResult { ExitCode = process.ExitCode, LogTail = process.ExitCode == 0 ? null : Tail(logPath) };
        }

        private static string? Tail(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var lines = File.ReadAllLines(path);
                return string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - 5)));
            }
            catch (IOException) { return null; }
        }
    }

    /// <summary>
    /// msg.exe ile oturum açmış tüm kullanıcılara (terminal server'daki tüm oturumlar dahil) mesaj gösterir.
    /// İlerleyen sürümde yerini tepsi uygulamasının bildirimleri alacak.
    /// </summary>
    internal sealed class MsgExeNotifier : IUserNotifier
    {
        private readonly IAgentLog _log;
        public MsgExeNotifier(IAgentLog log) => _log = log;

        public void NotifyAll(string message, TimeSpan displayFor)
        {
            try
            {
                var msg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msg.exe");
                var info = new ProcessStartInfo(msg, $"* /TIME:{(int)displayFor.TotalSeconds} \"{message.Replace("\"", "'")}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(info);
                p?.WaitForExit(10000);
            }
            catch (Exception ex)
            {
                _log.Error("Kullanıcılara mesaj gönderilemedi", ex);
            }
        }
    }

    internal sealed class FileAgentLog : IAgentLog
    {
        private readonly string _directory;
        private readonly bool _console;
        private readonly object _lock = new object();

        public FileAgentLog(string directory, bool console)
        {
            _directory = directory;
            _console = console;
        }

        public void Info(string message) => Write("BİLGİ", message);

        public void Error(string message, Exception? ex = null) => Write("HATA", ex == null ? message : $"{message}: {ex.Message}");

        private void Write(string level, string message)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            lock (_lock)
            {
                if (_console) Console.WriteLine(line);
                try
                {
                    Directory.CreateDirectory(_directory);
                    File.AppendAllText(Path.Combine(_directory, $"agent-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
                }
                catch (IOException) { }
            }
        }
    }

    internal static class MachineIdentity
    {
        /// <summary>Windows kurulumuna özgü MachineGuid; lisans bu değere bağlanır.</summary>
        public static string GetMachineId()
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string;
            if (string.IsNullOrWhiteSpace(guid)) throw new InvalidOperationException("MachineGuid okunamadı.");
            return Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("bideploy:" + guid));
        }
    }
}

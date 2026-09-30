using System;
using System.IO;
using System.Text.Json;
using BiDeploy.Core;

namespace BiDeploy.Agent.Core
{
    /// <summary>Servis yeniden başlasa da korunması gereken durum (state.json).</summary>
    public sealed class AgentState
    {
        // Sunucu rolü
        public string? DeviceToken { get; set; }
        public string? CompanyTitle { get; set; }
        public DateTime? LicenseExpiresAtUtc { get; set; }
        public string? PrestagedPackageId { get; set; }
        public string? ReleasedPackageId { get; set; }
        public DateTime? ReleasedAtUtc { get; set; }

        // İstemci rolü
        public string? WaitingForCloseSincePackageId { get; set; }
        public DateTime? WaitingForCloseSinceUtc { get; set; }
        public string? LastFailedPackageId { get; set; }
        public DateTime? LastFailedAtUtc { get; set; }
        public string? LastError { get; set; }

        public static AgentState Load(string path)
        {
            if (!File.Exists(path)) return new AgentState();
            return JsonSerializer.Deserialize<AgentState>(File.ReadAllText(path), JsonDefaults.Options) ?? new AgentState();
        }

        private static readonly object SaveLock = new object();

        /// <summary>Sunucu+istemci rolü aynı makinedeyse iki rol aynı örneği paylaşır; kayıt tek seferde yapılır.</summary>
        public void Save(string path)
        {
            lock (SaveLock) SaveCore(path);
        }

        private void SaveCore(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonDefaults.Options));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}

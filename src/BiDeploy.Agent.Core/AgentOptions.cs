using System;
using System.IO;
using System.Text.Json;
using BiDeploy.Core;

namespace BiDeploy.Agent.Core
{
    [Flags]
    public enum AgentRole
    {
        None = 0,
        Server = 1,
        Client = 2,
        /// <summary>Mikro sunucusu aynı zamanda terminal server ise.</summary>
        Both = Server | Client,
    }

    /// <summary>agent.json dosyasından okunan ayarlar.</summary>
    public sealed class AgentOptions
    {
        public AgentRole Role { get; set; } = AgentRole.Client;

        /// <summary>Sunucu rolü: VPS adresi, örn. https://deploy.biyazilim.com.tr</summary>
        public string VpsUrl { get; set; } = "";

        public string Product { get; set; } = "Fly";

        public string Architecture { get; set; } = "x64";

        /// <summary>İstemci rolü: sunucu ajanının adresi, örn. http://MIKROSUNUCU:8765/</summary>
        public string ServerUrl { get; set; } = "";

        /// <summary>Sunucu rolü: yerel ağda dinlenecek adres.</summary>
        public string LanListenPrefix { get; set; } = "http://+:8765/";

        /// <summary>İstemcilerin sunucu ajanına bağlanırken kullandığı firma anahtarı.</summary>
        public string LanKey { get; set; } = "";

        /// <summary>Sunucu rolü: sunucudaki bayi ekranının şifresi. İstemcilere dağıtılmaz (LanKey'den farklı olmalı).</summary>
        public string AdminPassword { get; set; } = "";

        public string DataDirectory { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BiDeploy");

        public int ServerPollSeconds { get; set; } = 300;

        public int ClientPollSeconds { get; set; } = 60;

        public int ForceCloseAfterMinutes { get; set; } = 10;

        public int InstallTimeoutMinutes { get; set; } = 30;

        /// <summary>Başarısız kurulumdan sonra yeniden denemeden önce beklenecek süre.</summary>
        public int RetryAfterFailureMinutes { get; set; } = 15;

        public string PackagesDirectory => Path.Combine(DataDirectory, "packages");
        public string LogsDirectory => Path.Combine(DataDirectory, "logs");
        public string StateFile => Path.Combine(DataDirectory, "state.json");

        public static AgentOptions Load(string path)
        {
            var options = JsonSerializer.Deserialize<AgentOptions>(File.ReadAllText(path), JsonDefaults.Options)
                ?? throw new InvalidOperationException("agent.json boş.");
            options.Validate();
            return options;
        }

        public void Validate()
        {
            if (Role == AgentRole.None) throw new InvalidOperationException("Role ayarlanmalı (Server, Client veya Both).");
            if (Role.HasFlag(AgentRole.Server) && string.IsNullOrWhiteSpace(VpsUrl))
                throw new InvalidOperationException("Sunucu rolü için VpsUrl gerekli.");
            if (Role == AgentRole.Client && string.IsNullOrWhiteSpace(ServerUrl))
                throw new InvalidOperationException("İstemci rolü için ServerUrl gerekli.");
            if (Role.HasFlag(AgentRole.Server) && (AdminPassword.Length < 8 || AdminPassword == LanKey))
                throw new InvalidOperationException("Sunucu rolü için en az 8 karakterlik, LanKey'den farklı bir AdminPassword gerekli.");
            if (string.IsNullOrWhiteSpace(LanKey) || LanKey.Length < 16)
                throw new InvalidOperationException("LanKey en az 16 karakter olmalı.");
        }
    }
}

using System;
using System.Collections.Generic;

namespace BiDeploy.Core.Protocol
{
    // ---- Sunucu ajanı <-> VPS ----

    public sealed class ActivationRequest
    {
        public string ActivationCode { get; set; } = "";
        public string Vkn { get; set; } = "";
        public string MachineId { get; set; } = "";
        public string MachineName { get; set; } = "";
    }

    public sealed class ActivationResponse
    {
        public string DeviceToken { get; set; } = "";
        public string CompanyTitle { get; set; } = "";
        public DateTime LicenseExpiresAtUtc { get; set; }
    }

    public sealed class LatestPackageResponse
    {
        /// <summary>Lisans süresi bittiyse true; bu durumda yeni paket verilmez ama ajan çalışmaya devam eder.</summary>
        public bool LicenseExpired { get; set; }
        public DateTime LicenseExpiresAtUtc { get; set; }
        public SignedManifest? Package { get; set; }
    }

    public sealed class ServerReport
    {
        public string AgentVersion { get; set; } = "";
        public string? ServerMikroVersion { get; set; }
        public string? PrestagedVersion { get; set; }
        public string? ReleasedVersion { get; set; }
        public List<ClientStatus> Clients { get; set; } = new List<ClientStatus>();
    }

    // ---- İstemci ajanı <-> sunucu ajanı (yerel ağ) ----

    public sealed class LanTargetResponse
    {
        /// <summary>İstemcilerin önceden indirip hazır bekletmesi gereken en yeni paket.</summary>
        public SignedManifest? Prestage { get; set; }

        /// <summary>Kurulumu serbest bırakılmış paket (sunucu bu sürüme güncellendi). null ise kurulum yapılmaz.</summary>
        public SignedManifest? Release { get; set; }

        public DateTime? ReleasedAtUtc { get; set; }

        /// <summary>Mikro açıksa uyarıdan kaç dakika sonra zorla kapatılacağı.</summary>
        public int ForceCloseAfterMinutes { get; set; } = 10;
    }

    public enum ClientState
    {
        Unknown = 0,
        UpToDate = 1,
        Downloading = 2,
        Ready = 3,
        WaitingForMikroToClose = 4,
        Installing = 5,
        Failed = 6,
    }

    public sealed class ClientStatus
    {
        public string MachineName { get; set; } = "";
        public string? InstalledVersion { get; set; }
        public string? PrestagedVersion { get; set; }
        public ClientState State { get; set; }
        public bool MikroRunning { get; set; }
        public string? LastError { get; set; }
        public DateTime ReportedAtUtc { get; set; }
    }
}

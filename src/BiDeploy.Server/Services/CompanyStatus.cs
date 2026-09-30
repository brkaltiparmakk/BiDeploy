using System.Text.Json;
using BiDeploy.Core;
using BiDeploy.Core.Protocol;
using BiDeploy.Server.Data;

namespace BiDeploy.Server.Services;

public enum LicenseHealth { NotAssigned, NotActivated, Active, ExpiringSoon, Expired }

/// <summary>Panelde gösterilecek, lisans kaydı ve sunucu ajanının son raporundan hesaplanan durum özeti.</summary>
public sealed class CompanyStatus
{
    public const int ExpiryWarningDays = 30;
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(15);

    public required Company Company { get; init; }
    public LicenseHealth License { get; init; }
    public int? DaysLeft { get; init; }
    public bool ServerOnline { get; init; }
    public ServerReport? Report { get; init; }
    public int ClientCount => Report?.Clients.Count ?? 0;
    public int UpToDate => Count(ClientState.UpToDate);
    public int Failed => Count(ClientState.Failed);
    public int WaitingForClose => Count(ClientState.WaitingForMikroToClose);
    public int MikroOpen => Report?.Clients.Count(c => c.MikroRunning) ?? 0;

    private int Count(ClientState state) => Report?.Clients.Count(c => c.State == state) ?? 0;

    public static CompanyStatus From(Company company, DateTime nowUtc)
    {
        var license = company.License;
        LicenseHealth health;
        int? daysLeft = null;
        if (license == null) health = LicenseHealth.NotAssigned;
        else if (license.DeviceTokenHash == null || license.ExpiresAtUtc == null) health = LicenseHealth.NotActivated;
        else
        {
            daysLeft = (int)Math.Ceiling((license.ExpiresAtUtc.Value - nowUtc).TotalDays);
            health = daysLeft <= 0 ? LicenseHealth.Expired
                : daysLeft <= ExpiryWarningDays ? LicenseHealth.ExpiringSoon
                : LicenseHealth.Active;
        }

        return new CompanyStatus
        {
            Company = company,
            License = health,
            DaysLeft = daysLeft,
            ServerOnline = license?.LastSeenAtUtc != null && nowUtc - license.LastSeenAtUtc.Value < OfflineAfter,
            Report = license?.LastReportJson == null ? null : JsonSerializer.Deserialize<ServerReport>(license.LastReportJson, JsonDefaults.Options),
        };
    }

    public static string StateText(ClientState state) => state switch
    {
        ClientState.UpToDate => "Güncel",
        ClientState.Downloading => "İndiriliyor",
        ClientState.Ready => "Hazır (kurulum bekliyor)",
        ClientState.WaitingForMikroToClose => "Mikro'nun kapanması bekleniyor",
        ClientState.Installing => "Kuruluyor",
        ClientState.Failed => "Hata",
        _ => "Bilinmiyor",
    };

    public static string StateCss(ClientState state) => state switch
    {
        ClientState.UpToDate => "ok",
        ClientState.Failed => "bad",
        ClientState.WaitingForMikroToClose or ClientState.Installing => "warn",
        _ => "muted",
    };
}

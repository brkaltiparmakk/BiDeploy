namespace BiDeploy.Server.Data;

/// <summary>Mikro bayisi (yazılım şirketi). Lisans havuzundaki boş lisans sayısını tutar.</summary>
public class Dealer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public int AvailableLicenses { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<Company> Companies { get; set; } = new();
}

/// <summary>Bayinin müşterisi. Bir VKN = bir Mikro sunucusu = bir lisans.</summary>
public class Company
{
    public int Id { get; set; }
    public int DealerId { get; set; }
    public Dealer Dealer { get; set; } = null!;
    public string Vkn { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public License? License { get; set; }
}

public class License
{
    public int Id { get; set; }
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
    public string ActivationCode { get; set; } = "";
    public DateTime IssuedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string? MachineId { get; set; }
    public string? MachineName { get; set; }
    public string? DeviceTokenHash { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public string? LastReportJson { get; set; }

    public bool IsExpired(DateTime nowUtc) => ExpiresAtUtc.HasValue && ExpiresAtUtc.Value < nowUtc;
}

/// <summary>BiYazılım'ın yayınladığı imzalı setup paketi.</summary>
public class Package
{
    public int Id { get; set; }
    public string PackageId { get; set; } = "";
    public string Product { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Sıralama için sıfır dolgulu sürüm, örn. 00017.00007.00004.46277.</summary>
    public string VersionSortKey { get; set; } = "";
    public string ManifestJson { get; set; } = "";
    public string Signature { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public long FileSize { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    /// <summary>Sorunlu bir sürüm geri çekilirse false yapılır; ajanlara artık sunulmaz.</summary>
    public bool IsActive { get; set; } = true;
}

using BiDeploy.Core;

namespace BiDeploy.Server;

public class ServerOptions
{
    /// <summary>BiYazılım yönetici API anahtarı (X-Admin-Key). Ortam değişkeni: BiDeploy__AdminKey</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>Yayıncı açık anahtarı (publisher.pub.json) yolu. VPS paketleri yüklerken imzayı bununla doğrular.</summary>
    public string PublisherPublicKeyPath { get; set; } = "";

    /// <summary>Setup dosyalarının saklandığı klasör.</summary>
    public string StorageDirectory { get; set; } = "storage";

    /// <summary>"Sqlite" (geliştirme) veya "Postgres" (üretim).</summary>
    public string DatabaseProvider { get; set; } = "Sqlite";

    public int LicenseDays { get; set; } = 365;

    public PackageSigning.KeyFile LoadPublisherKey()
    {
        if (string.IsNullOrWhiteSpace(PublisherPublicKeyPath) || !File.Exists(PublisherPublicKeyPath))
            throw new InvalidOperationException($"Yayıncı açık anahtarı bulunamadı: '{PublisherPublicKeyPath}'");
        return PackageSigning.KeyFile.FromJson(File.ReadAllText(PublisherPublicKeyPath)).PublicOnly();
    }
}

using System.Diagnostics;
using System.Net.Http.Headers;
using BiDeploy.Core;

namespace BiDeploy.Publisher;

public static class PublisherCli
{
    public const string DefaultInstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS";

    private const string Usage = """
        BiDeploy Yayıncı

        Anahtar üretimi (bir kez):
          bideploy-publish keygen --out <klasör>

        Paket yayınlama:
          bideploy-publish publish <setup.exe> --key <publisher.key.json> --server <https://vps> --admin-key <anahtar>
              [--product Fly] [--arch x64] [--version 17.7.4.46277] [--main-exe MikroFly.exe] [--args "..."]

        Sadece imzala (yüklemeden):
          bideploy-publish sign <setup.exe> --key <publisher.key.json> --out <klasör> [diğer seçenekler]
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, HttpMessageHandler? handler = null)
    {
        try
        {
            if (args.Length == 0) { output.WriteLine(Usage); return 1; }
            var command = args[0];
            var opts = ParseOptions(args.Skip(1).ToArray(), out var positional);

            switch (command)
            {
                case "keygen":
                    return KeyGen(Required(opts, "out"), output);
                case "sign":
                {
                    var (manifestBytes, signature, _) = CreateSignedManifest(Positional(positional), opts, output);
                    var outDir = Required(opts, "out");
                    Directory.CreateDirectory(outDir);
                    await File.WriteAllBytesAsync(Path.Combine(outDir, "manifest.json"), manifestBytes);
                    await File.WriteAllTextAsync(Path.Combine(outDir, "manifest.sig"), signature);
                    output.WriteLine($"İmzalandı: {outDir}");
                    return 0;
                }
                case "publish":
                {
                    var setup = Positional(positional);
                    var (manifestBytes, signature, manifest) = CreateSignedManifest(setup, opts, output);
                    await UploadAsync(Required(opts, "server"), Required(opts, "admin-key"), setup, manifestBytes, signature, handler);
                    output.WriteLine($"Yayınlandı: {manifest.PackageId}");
                    return 0;
                }
                default:
                    output.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or HttpRequestException or PackageVerificationException or FormatException)
        {
            error.WriteLine("Hata: " + ex.Message);
            return 2;
        }
    }

    private static int KeyGen(string outDir, TextWriter output)
    {
        Directory.CreateDirectory(outDir);
        var privatePath = Path.Combine(outDir, "publisher.key.json");
        if (File.Exists(privatePath)) throw new InvalidOperationException($"{privatePath} zaten var; üzerine yazılmaz.");
        var key = PackageSigning.GenerateKey();
        File.WriteAllText(privatePath, key.ToJson());
        File.WriteAllText(Path.Combine(outDir, "publisher.pub.json"), key.PublicOnly().ToJson());
        output.WriteLine($"Anahtar üretildi (KeyId {key.KeyId}).");
        output.WriteLine($"  Özel anahtar : {privatePath}  -> SADECE yayıncı bilgisayarında saklayın, yedekleyin, kimseyle paylaşmayın.");
        output.WriteLine($"  Açık anahtar : {Path.Combine(outDir, "publisher.pub.json")}  -> ajana gömülür ve VPS'e konur.");
        return 0;
    }

    internal static (byte[] ManifestBytes, string Signature, PackageManifest Manifest) CreateSignedManifest(
        string setupPath, Dictionary<string, string> opts, TextWriter output)
    {
        if (!File.Exists(setupPath)) throw new ArgumentException($"Setup dosyası bulunamadı: {setupPath}");
        var key = PackageSigning.KeyFile.FromJson(File.ReadAllText(Required(opts, "key")));

        SetupFileName.TryInfer(setupPath, out var inferredProduct, out var inferredArch);
        var product = opts.GetValueOrDefault("product") ?? inferredProduct;
        var arch = opts.GetValueOrDefault("arch") ?? inferredArch;
        if (product.Length == 0 || arch.Length == 0)
            throw new ArgumentException("Ürün/mimari dosya adından çıkarılamadı; --product ve --arch verin.");

        var version = opts.GetValueOrDefault("version") ?? ReadFileVersion(setupPath)
            ?? throw new ArgumentException("Setup sürümü okunamadı; --version verin.");

        var manifest = new PackageManifest
        {
            Product = product,
            Architecture = arch,
            Version = MikroVersion.Parse(version).ToString(),
            FileName = Path.GetFileName(setupPath),
            FileSize = new FileInfo(setupPath).Length,
            Sha256 = Hashing.Sha256HexOfFile(setupPath),
            InstallerArguments = opts.GetValueOrDefault("args") ?? DefaultInstallerArguments,
            MainExecutable = opts.GetValueOrDefault("main-exe") ?? $"Mikro{product}.exe",
            PublishedAtUtc = DateTime.UtcNow,
        };
        manifest.Validate();

        var bytes = manifest.ToJsonBytes();
        var signature = PackageSigning.Sign(bytes, key);
        // Kendi imzamızı açık anahtarla doğrulayarak yanlış anahtar dosyasını erkenden yakala.
        PackageSigning.VerifyManifest(new SignedManifest { ManifestJson = System.Text.Encoding.UTF8.GetString(bytes), Signature = signature }, key.PublicOnly());

        output.WriteLine($"Paket: {manifest.PackageId}  ({manifest.FileSize / (1024 * 1024)} MB, SHA-256 {manifest.Sha256[..12]}…)");
        return (bytes, signature, manifest);
    }

    private static string? ReadFileVersion(string path)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        var text = string.IsNullOrWhiteSpace(info.FileVersion) ? info.ProductVersion : info.FileVersion;
        return MikroVersion.TryParse(text, out _) ? text : null;
    }

    private static async Task UploadAsync(string server, string adminKey, string setupPath, byte[] manifestBytes, string signature, HttpMessageHandler? handler)
    {
        using var http = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromHours(1);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(manifestBytes), "manifest", "manifest.json");
        form.Add(new StringContent(signature), "signature");
        await using var setupStream = File.OpenRead(setupPath);
        var setupContent = new StreamContent(setupStream);
        setupContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(setupContent, "setup", Path.GetFileName(setupPath));

        using var request = new HttpRequestMessage(HttpMethod.Post, server.TrimEnd('/') + "/api/admin/packages") { Content = form };
        request.Headers.Add("X-Admin-Key", adminKey);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Yükleme başarısız ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
    }

    private static Dictionary<string, string> ParseOptions(string[] args, out List<string> positional)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} için değer eksik.");
                result[args[i][2..]] = args[++i];
            }
            else positional.Add(args[i]);
        }
        return result;
    }

    private static string Required(Dictionary<string, string> opts, string name) =>
        opts.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"--{name} gerekli.");

    private static string Positional(List<string> positional) =>
        positional.Count == 1 ? positional[0] : throw new ArgumentException("Tek bir setup dosyası yolu verin.");
}

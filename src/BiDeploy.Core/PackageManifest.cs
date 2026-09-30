using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiDeploy.Core
{
    /// <summary>
    /// Dağıtılan bir setup dosyasının imzalanan tanımı. Ajanlar yalnızca BiYazılım'ın özel anahtarıyla
    /// imzalanmış manifestleri kabul eder; kurulum parametreleri de imzanın kapsamındadır.
    /// </summary>
    public sealed class PackageManifest
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>Örn. "Fly", ileride "Jump" vb.</summary>
        public string Product { get; set; } = "";

        /// <summary>"x64" veya "x86".</summary>
        public string Architecture { get; set; } = "";

        /// <summary>Setup exe'sinin FileVersion değeri, örn. "17.7.4.46277".</summary>
        public string Version { get; set; } = "";

        public string FileName { get; set; } = "";

        public long FileSize { get; set; }

        /// <summary>Setup dosyasının SHA-256 özeti (küçük harf hex).</summary>
        public string Sha256 { get; set; } = "";

        public string InstallerArguments { get; set; } = "";

        /// <summary>Kurulu programın ana exe'si, örn. "MikroFly.exe". Sürüm tespiti ve kapatma için kullanılır.</summary>
        public string MainExecutable { get; set; } = "";

        public DateTime PublishedAtUtc { get; set; }

        [JsonIgnore]
        public string PackageId => MakePackageId(Product, Architecture, Version);

        public static string MakePackageId(string product, string architecture, string version) =>
            $"{product}-{architecture}-{version}";

        [JsonIgnore]
        public string ProcessName =>
            MainExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? MainExecutable.Substring(0, MainExecutable.Length - 4)
                : MainExecutable;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        public byte[] ToJsonBytes() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

        public static PackageManifest Parse(byte[] json)
        {
            var manifest = JsonSerializer.Deserialize<PackageManifest>(json, JsonOptions)
                ?? throw new FormatException("Manifest boş.");
            manifest.Validate();
            return manifest;
        }

        public void Validate()
        {
            var errors = new List<string>();
            if (SchemaVersion != CurrentSchemaVersion) errors.Add($"Desteklenmeyen şema sürümü: {SchemaVersion}");
            if (!IsSafeToken(Product)) errors.Add("Ürün adı geçersiz.");
            if (Architecture != "x64" && Architecture != "x86") errors.Add("Mimari x64 veya x86 olmalı.");
            if (!MikroVersion.TryParse(Version, out _)) errors.Add("Sürüm geçersiz.");
            if (string.IsNullOrWhiteSpace(FileName) || FileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) errors.Add("Dosya adı geçersiz.");
            if (FileSize <= 0) errors.Add("Dosya boyutu geçersiz.");
            if (Sha256.Length != 64) errors.Add("SHA-256 geçersiz.");
            if (string.IsNullOrWhiteSpace(MainExecutable) || MainExecutable.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) errors.Add("Ana exe adı geçersiz.");
            if (errors.Count > 0) throw new FormatException(string.Join(" ", errors));
        }

        private static bool IsSafeToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 32) return false;
            foreach (var c in value)
                if (!(c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9')) return false;
            return true;
        }

        public override string ToString() => PackageId;
    }

    /// <summary>İmzalı manifest: imza, manifestin ham JSON baytları üzerindedir (yeniden serileştirme yok).</summary>
    public sealed class SignedManifest
    {
        public string ManifestJson { get; set; } = "";

        /// <summary>Base64 RSA-SHA256 imzası.</summary>
        public string Signature { get; set; } = "";

        public byte[] ManifestBytes() => Encoding.UTF8.GetBytes(ManifestJson);
    }
}

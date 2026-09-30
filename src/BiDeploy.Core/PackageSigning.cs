using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace BiDeploy.Core
{
    /// <summary>
    /// RSA-3072 / SHA-256 / PKCS#1 v1.5 imzası. .NET Framework 4.8 ve .NET 10'da ek kütüphane olmadan çalışır.
    /// Özel anahtar sadece BiYazılım'ın yayıncı bilgisayarında durur; ajanlara ve VPS'e yalnızca açık anahtar gider.
    /// </summary>
    public static class PackageSigning
    {
        public const int KeySizeBits = 3072;

        public sealed class KeyFile
        {
            public string KeyId { get; set; } = "";
            public string Modulus { get; set; } = "";
            public string Exponent { get; set; } = "";
            public string? D { get; set; }
            public string? P { get; set; }
            public string? Q { get; set; }
            public string? DP { get; set; }
            public string? DQ { get; set; }
            public string? InverseQ { get; set; }

            public bool HasPrivateKey => D != null;

            public KeyFile PublicOnly() => new KeyFile { KeyId = KeyId, Modulus = Modulus, Exponent = Exponent };

            public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

            public static KeyFile FromJson(string json) =>
                JsonSerializer.Deserialize<KeyFile>(json) ?? throw new FormatException("Anahtar dosyası boş.");

            internal RSA ToRsa()
            {
                var p = new RSAParameters
                {
                    Modulus = Convert.FromBase64String(Modulus),
                    Exponent = Convert.FromBase64String(Exponent),
                };
                if (HasPrivateKey)
                {
                    p.D = Convert.FromBase64String(D!);
                    p.P = Convert.FromBase64String(P!);
                    p.Q = Convert.FromBase64String(Q!);
                    p.DP = Convert.FromBase64String(DP!);
                    p.DQ = Convert.FromBase64String(DQ!);
                    p.InverseQ = Convert.FromBase64String(InverseQ!);
                }
                var rsa = RSA.Create();
                rsa.ImportParameters(p);
                return rsa;
            }
        }

        public static KeyFile GenerateKey()
        {
            using var rsa = RSA.Create();
            rsa.KeySize = KeySizeBits;
            var p = rsa.ExportParameters(true);
            return new KeyFile
            {
                KeyId = Hashing.Sha256Hex(p.Modulus!).Substring(0, 16),
                Modulus = Convert.ToBase64String(p.Modulus!),
                Exponent = Convert.ToBase64String(p.Exponent!),
                D = Convert.ToBase64String(p.D!),
                P = Convert.ToBase64String(p.P!),
                Q = Convert.ToBase64String(p.Q!),
                DP = Convert.ToBase64String(p.DP!),
                DQ = Convert.ToBase64String(p.DQ!),
                InverseQ = Convert.ToBase64String(p.InverseQ!),
            };
        }

        public static string Sign(byte[] data, KeyFile privateKey)
        {
            if (!privateKey.HasPrivateKey) throw new InvalidOperationException("İmzalamak için özel anahtar gerekir.");
            using var rsa = privateKey.ToRsa();
            return Convert.ToBase64String(rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        public static bool Verify(byte[] data, string signatureBase64, KeyFile publicKey)
        {
            byte[] signature;
            try { signature = Convert.FromBase64String(signatureBase64); }
            catch (FormatException) { return false; }
            using var rsa = publicKey.ToRsa();
            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        /// <summary>İmzayı doğrular ve manifesti döner; imza tutmazsa istisna fırlatır.</summary>
        public static PackageManifest VerifyManifest(SignedManifest signed, KeyFile publicKey)
        {
            var bytes = signed.ManifestBytes();
            if (!Verify(bytes, signed.Signature, publicKey))
                throw new PackageVerificationException("Manifest imzası geçersiz. Paket BiYazılım tarafından yayınlanmamış.");
            try { return PackageManifest.Parse(bytes); }
            catch (Exception ex) when (ex is FormatException || ex is JsonException)
            {
                throw new PackageVerificationException("İmzalı manifest okunamadı: " + ex.Message);
            }
        }

        /// <summary>İndirilen setup dosyasının boyut ve SHA-256 değerini manifestle karşılaştırır.</summary>
        public static void VerifyFile(string path, PackageManifest manifest)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new PackageVerificationException($"Dosya bulunamadı: {path}");
            if (info.Length != manifest.FileSize)
                throw new PackageVerificationException($"Dosya boyutu uyuşmuyor ({info.Length} != {manifest.FileSize}).");
            var hash = Hashing.Sha256HexOfFile(path);
            if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new PackageVerificationException("Dosya özeti (SHA-256) uyuşmuyor.");
        }
    }

    public sealed class PackageVerificationException : Exception
    {
        public PackageVerificationException(string message) : base(message) { }
    }

    public static class Hashing
    {
        public static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(data));
        }

        public static string Sha256HexOfFile(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return ToHex(sha.ComputeHash(stream));
        }

        public static string ToHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                var b = bytes[i];
                chars[i * 2] = "0123456789abcdef"[b >> 4];
                chars[i * 2 + 1] = "0123456789abcdef"[b & 0xF];
            }
            return new string(chars);
        }
    }
}

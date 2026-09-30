using System.Security.Cryptography;
using System.Text;
using BiDeploy.Core;

namespace BiDeploy.Server;

/// <summary>API anahtarları ve cihaz jetonları veritabanında sadece SHA-256 özeti olarak tutulur.</summary>
public static class Secrets
{
    public static string NewToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string secret) => Hashing.Sha256Hex(Encoding.UTF8.GetBytes(secret));

    /// <summary>Okuması kolay aktivasyon kodu, örn. K7QM-2XPA-9RTD (karışan 0/O, 1/I harfleri yok).</summary>
    public static string NewActivationCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        var s = new string(chars);
        return $"{s[..4]}-{s[4..8]}-{s[8..]}";
    }

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string VersionSortKey(string version)
    {
        var v = MikroVersion.Parse(version);
        return $"{v.Major:D5}.{v.Minor:D5}.{Math.Max(v.Build, 0):D5}.{Math.Max(v.Revision, 0):D10}";
    }
}

using System.Text;
using BiDeploy.Core;
using Xunit;

namespace BiDeploy.Tests;

public class CoreTests
{
    private static readonly PackageSigning.KeyFile Key = PackageSigning.GenerateKey();

    private static PackageManifest SampleManifest() => new()
    {
        Product = "Fly", Architecture = "x64", Version = "17.7.4.46277", FileName = "Fly_v17xx_Client_Setupx064.exe",
        FileSize = 10, Sha256 = new string('a', 64), InstallerArguments = "/VERYSILENT", MainExecutable = "MikroFly.exe",
    };

    [Fact]
    public void Signed_manifest_verifies_with_public_key()
    {
        var bytes = SampleManifest().ToJsonBytes();
        var signed = new SignedManifest { ManifestJson = Encoding.UTF8.GetString(bytes), Signature = PackageSigning.Sign(bytes, Key) };
        var manifest = PackageSigning.VerifyManifest(signed, Key.PublicOnly());
        Assert.Equal("Fly-x64-17.7.4.46277", manifest.PackageId);
        Assert.Equal("MikroFly", manifest.ProcessName);
    }

    [Fact]
    public void Tampered_manifest_is_rejected()
    {
        var bytes = SampleManifest().ToJsonBytes();
        var signature = PackageSigning.Sign(bytes, Key);
        var tampered = Encoding.UTF8.GetString(bytes).Replace("/VERYSILENT", "/VERYSILENT & kotu.exe");
        Assert.Throws<PackageVerificationException>(() =>
            PackageSigning.VerifyManifest(new SignedManifest { ManifestJson = tampered, Signature = signature }, Key.PublicOnly()));
    }

    [Fact]
    public void Manifest_signed_by_another_key_is_rejected()
    {
        var bytes = SampleManifest().ToJsonBytes();
        var otherKey = PackageSigning.GenerateKey();
        var signed = new SignedManifest { ManifestJson = Encoding.UTF8.GetString(bytes), Signature = PackageSigning.Sign(bytes, otherKey) };
        Assert.Throws<PackageVerificationException>(() => PackageSigning.VerifyManifest(signed, Key.PublicOnly()));
    }

    [Fact]
    public void Public_key_cannot_sign()
    {
        Assert.Throws<InvalidOperationException>(() => PackageSigning.Sign(new byte[] { 1 }, Key.PublicOnly()));
    }

    [Theory]
    [InlineData("1111111114", true)]
    [InlineData("1111111111", false)]
    [InlineData("12345678950", true)]  // TCKN
    [InlineData("12345678951", false)]
    [InlineData("01234567890", false)] // TCKN 0 ile başlayamaz
    [InlineData("12345", false)]
    [InlineData("abcdefghij", false)]
    [InlineData(null, false)]
    public void Tax_id_validation(string? value, bool expected) => Assert.Equal(expected, TaxId.IsValid(value));

    [Theory]
    [InlineData("17.7.4.46277", "17.7.4.46277", 0)]
    [InlineData("17.7.4.46277", "17.7.4.46300", -1)]
    [InlineData("17.8", "17.7.9.99999", 1)]
    [InlineData("17.7", "17.7.0.0", 0)]
    [InlineData(null, "17.7.4.46277", -1)]
    public void Version_comparison(string? a, string? b, int expectedSign) =>
        Assert.Equal(expectedSign, Math.Sign(MikroVersion.Compare(a, b)));

    [Theory]
    [InlineData(@"C:\Dosya\Fly_v17xx_Client_Setupx064.exe", "Fly", "x64")]
    [InlineData("Jump_v17xx_Client_Setupx086.exe", "Jump", "x86")]
    public void Setup_file_name_inference(string path, string product, string arch)
    {
        Assert.True(SetupFileName.TryInfer(path, out var p, out var a));
        Assert.Equal(product, p);
        Assert.Equal(arch, a);
    }
}

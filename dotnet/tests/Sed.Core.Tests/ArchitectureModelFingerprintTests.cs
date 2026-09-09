using System.Security.Cryptography;
using System.Text;
using Sed.Formats.ThreeDo;

namespace Sed.Core.Tests;

public sealed class ArchitectureModelFingerprintTests
{
    [Fact]
    public void MatchesWindowsDelphiNormalizedUtf8WithTrailingCrLf()
    {
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SECTION: HEADER\r\n3DO 2.1\r\n"))).ToLowerInvariant();
        Assert.Equal(expected, ArchitectureModelFingerprint.Compute(" # comment\r\n SECTION: HEADER  # ignored\r\n\r\n 3DO 2.1 \n"));
        Assert.Equal(expected, ArchitectureModelFingerprint.Compute("SECTION: HEADER\n3DO 2.1"));
        Assert.NotEqual(expected, ArchitectureModelFingerprint.Compute("section: HEADER\n3DO 2.1"));
        Assert.NotEqual(expected, ArchitectureModelFingerprint.Compute("SECTION:  HEADER\n3DO 2.1"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ArchitectureModelFingerprint.Compute("# only comment\n"));
    }

    [Theory]
    [InlineData("name café")][InlineData("name\0hidden")]
    public void UnsupportedLegacyEncodingCannotCertifyEquivalence(string source)
        => Assert.Null(ArchitectureModelFingerprint.Compute(source));

    [Fact]
    public void MatchesDelphiBoundaryControlTrimmingWithoutChangingInteriorControls()
    {
        string expected = ArchitectureModelFingerprint.Compute("SECTION: HEADER")!;
        Assert.Equal(expected, ArchitectureModelFingerprint.Compute("\u001cSECTION: HEADER\u001c"));
        Assert.NotEqual(expected, ArchitectureModelFingerprint.Compute("SECTION:\u001c HEADER"));
    }

    [Fact]
    public void NativeLineBufferLimitIsRespected()
        => Assert.Null(ArchitectureModelFingerprint.Compute(new string('a', 4096)));
}

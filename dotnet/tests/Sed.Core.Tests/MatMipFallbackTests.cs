using System.Buffers.Binary;
using System.Text;
using Sed.Formats.Material;
using Xunit;

namespace Sed.Core.Tests;

/// <summary>
/// Retail-like mip fallback: when the selected texture's mip 0 is complete but a later mip is
/// cut short, retail JK's unchecked reads still show mip 0 on faces nearer than the level's
/// MipMap Distances (farther faces sample the never-filled level), so SED decodes mip 0 and
/// records a note saying so. DBZ Tournament ships five such 16-bit MATs (zgrass1, zgrass3,
/// walkway, budoukaisign, door2017); its MipMap Distances (200 200 300 400) exceed the arena,
/// so retail shows mip 0 there everywhere. A short mip 0, a short earlier texture or an
/// impossible mip count stay refused.
/// </summary>
public class MatMipFallbackTests
{
    private const string Truncated = "Truncated MAT mip pixel data.";

    private static string Note(int level, int count, int width, int height, int needs, int remain) =>
        $"Retail mip fallback: mip level {level} of {count} levels ({width}x{height}) needs {needs} bytes but {remain} remain; "
        + "showing complete mip level 0, as retail JK does on faces nearer than the level's MipMap Distances; "
        + $"retail faces far enough to use mip level {level} or later show data the file never filled.";

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void EveryDepthShowsCompleteMipZeroWhenTheLastMipIsMissing(int bpp)
    {
        // 4x4 with three mips (4x4, 2x2, 1x1); the file ends before the 1x1 mip.
        int bytesPerPixel = bpp / 8;
        var texture = Texture(4, 4, 3, bytesPerPixel, seed: 1);
        var bytes = Mat(bpp, 0, texture);
        int lastMip = bytesPerPixel; // 1x1
        var complete = MatFile.Read(bytes);
        Assert.Null(complete.MipFallbackNote);
        var fallback = MatFile.Read(bytes[..^lastMip]);
        Assert.Equal(Note(2, 3, 1, 1, bytesPerPixel, 0), fallback.MipFallbackNote);
        Assert.Equal(4, fallback.Width);
        Assert.Equal(4, fallback.Height);
        if (bpp == 8)
            Assert.Equal(complete.Indices, fallback.Indices);
        else
            Assert.Equal(complete.Rgba, fallback.Rgba);
    }

    [Fact]
    public void DbzZgrassPatternEndsExactlyAfterMipZeroOfFourLevels()
    {
        // zgrass1/zgrass3: 256x256 RGB565 with four declared mips; the file ends after mip 0.
        var mip0 = new byte[256 * 256 * 2];
        for (int i = 0; i < mip0.Length; i += 2) BinaryPrimitives.WriteUInt16LittleEndian(mip0.AsSpan(i), 0x07E0); // pure green
        var bytes = Mat(16, 0, new TextureData(256, 256, 0, 4, [mip0]));
        Assert.Equal(76 + 40 + 24 + mip0.Length, bytes.Length);
        var mat = MatFile.Read(bytes);
        Assert.Equal(Note(1, 4, 128, 128, 32768, 0), mat.MipFallbackNote);
        Assert.Equal("Retail mip fallback: mip level 1 of 4 levels (128x128) needs 32768 bytes but 0 remain; showing complete mip level 0, as retail JK does on faces nearer than the level's MipMap Distances; retail faces far enough to use mip level 1 or later show data the file never filled.",
            mat.MipFallbackNote);
        Assert.True(mat.IsTrueColor);
        Assert.Equal(256 * 256 * 4, mat.Rgba.Length);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, mat.Rgba[..4]);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, mat.Rgba[^4..]);
    }

    [Theory]
    [InlineData(512, 64)]   // walkway.mat
    [InlineData(256, 64)]   // budoukaisign.mat
    [InlineData(64, 128)]   // door2017.mat
    public void DbzPatternEndsAfterMipTwo(int width, int height)
    {
        var mips = new List<byte[]>();
        for (int level = 0; level < 3; level++) mips.Add(new byte[(width >> level) * (height >> level) * 2]);
        var bytes = Mat(16, 0, new TextureData(width, height, 0, 4, [.. mips]));
        var mat = MatFile.Read(bytes);
        Assert.Equal(Note(3, 4, width >> 3, height >> 3, (width >> 3) * (height >> 3) * 2, 0), mat.MipFallbackNote);
        Assert.Equal(width, mat.Width);
        Assert.Equal(height, mat.Height);
    }

    [Fact]
    public void PartialLaterMipReportsTheBytesThatRemain()
    {
        // 8x8 8-bit, four mips; mip 1 (4x4, 16 bytes) has only 5 bytes.
        var texture = Texture(8, 8, 4, 1, seed: 3);
        var bytes = Mat(8, 0, texture);
        int keep = 76 + 40 + 24 + 64 + 5;
        var mat = MatFile.Read(bytes[..keep]);
        Assert.Equal(Note(1, 4, 4, 4, 16, 5), mat.MipFallbackNote);
        Assert.Equal(texture.Mips[0], mat.Indices);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void ShortMipZeroIsRefusedWithTheExistingMessage(int bpp)
    {
        var bytes = Mat(bpp, 0, Texture(4, 4, 3, bpp / 8, seed: 5));
        int mip0End = 76 + 40 + 24 + 16 * (bpp / 8);
        var error = Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes[..(mip0End - 1)]));
        Assert.Equal(Truncated, error.Message);
        // Exactly mip 0 and nothing more is the fallback, not a refusal.
        Assert.NotNull(MatFile.Read(bytes[..mip0End]).MipFallbackNote);
    }

    [Fact]
    public void ShortEarlierTextureIsRefusedEvenWhenOnlyItsLastMipIsMissing()
    {
        // Cel 0 selects texture 1; texture 0's full chain locates it, so texture 0 must be complete.
        // 8x8 with four mips leaves room for both texture headers, so the mip walk is what refuses.
        var first = Texture(8, 8, 4, 1, seed: 7);
        var second = Texture(2, 1, 1, 1, seed: 9);
        var bytes = Mat(8, 1, first, second);
        Assert.Null(MatFile.Read(bytes).MipFallbackNote);
        int firstEnd = 76 + 40 + 24 + 64 + 16 + 4 + 1;
        var error = Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes[..(firstEnd - 1)]));
        Assert.Equal(Truncated, error.Message);
    }

    [Fact]
    public void FallbackAppliesToTheSelectedLaterTexture()
    {
        var first = Texture(2, 2, 2, 2, seed: 11);
        var second = Texture(4, 4, 3, 2, seed: 13);
        var bytes = Mat(16, 1, first, second);
        var complete = MatFile.Read(bytes);
        var mat = MatFile.Read(bytes[..^2]); // drop the selected texture's 1x1 mip
        Assert.Equal(Note(2, 3, 1, 1, 2, 0), mat.MipFallbackNote);
        Assert.Equal(complete.Rgba, mat.Rgba);
        Assert.Equal(4, mat.Width);
    }

    [Theory]
    [InlineData(4, 4, 4)]   // 4x4 has three levels
    [InlineData(8, 2, 3)]   // 8x2 has two levels before the height reaches zero
    [InlineData(1, 1, 2)]
    public void MipCountBeyondDimensionsIsRefusedEvenWhenTheFileIsTruncated(int width, int height, int mipCount)
    {
        // Only mip 0 is present, so a fallback would otherwise be possible.
        var bytes = Mat(8, 0, new TextureData(width, height, 0, mipCount, [new byte[width * height]]));
        var error = Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes));
        Assert.Equal("MAT mip count exceeds its dimensions.", error.Message);
    }

    [Fact]
    public void MipCountOutOfRangeStaysRefused()
    {
        var bytes = Mat(8, 0, new TextureData(4, 4, 0, 16, [new byte[16]]));
        Assert.Equal("MAT mip count is out of range.", Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes)).Message);
    }

    [Fact]
    public void ColourKeyAndMaskSurviveTheFallback()
    {
        var bytes = Mat(8, 0, new TextureData(2, 2, 1, 2, [[0, 7, 7, 0]]));
        var mat = MatFile.Read(bytes);
        Assert.True(mat.HasColorKey);
        Assert.Equal(new byte[] { 0, 7, 7, 0 }, mat.Indices);
        Assert.Equal(Note(1, 2, 1, 1, 1, 0), mat.MipFallbackNote);
    }

    private sealed record TextureData(int Width, int Height, int Flags, int MipCount, byte[][] Mips);

    /// <summary>A texture with a complete chain of <paramref name="mipCount"/> deterministic mips.</summary>
    private static TextureData Texture(int width, int height, int mipCount, int bytesPerPixel, int seed)
    {
        var mips = new byte[mipCount][];
        for (int level = 0; level < mipCount; level++)
        {
            var mip = new byte[(width >> level) * (height >> level) * bytesPerPixel];
            for (int i = 0; i < mip.Length; i++) mip[i] = (byte)(seed * 31 + level * 7 + i * 13);
            mips[level] = mip;
        }
        return new TextureData(width, height, 0, mipCount, mips);
    }

    /// <summary>
    /// A texture MAT of one cel. 8-bit is indexed; 16-bit declares RGB565; 32-bit declares ARGB8888.
    /// Each texture header declares <see cref="TextureData.MipCount"/> but stores only the given mips.
    /// </summary>
    private static byte[] Mat(int bpp, int selected, params TextureData[] textures)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("MAT "));
        writer.Write(0x32);
        writer.Write(2);
        writer.Write(1);
        writer.Write(textures.Length);
        (int mode, int[] bits, int[] shifts, int aBits, int aShift) = bpp switch
        {
            8 => (0, new[] { 0, 0, 0 }, new[] { 0, 0, 0 }, 0, 0),
            16 => (1, new[] { 5, 6, 5 }, new[] { 11, 5, 0 }, 0, 0),
            _ => (2, new[] { 8, 8, 8 }, new[] { 16, 8, 0 }, 8, 24),
        };
        writer.Write(mode);
        writer.Write(bpp);
        foreach (int value in bits) writer.Write(value);
        foreach (int value in shifts) writer.Write(value);
        foreach (int value in bits) writer.Write(bpp == 8 ? 0 : 8 - value);
        writer.Write(aBits); writer.Write(aShift); writer.Write(bpp == 8 ? 0 : 8 - aBits);
        writer.Write(8); // Textured cel; engine consumes its 16-byte extension.
        writer.Write(new byte[32]);
        writer.Write(selected);
        foreach (var texture in textures)
        {
            writer.Write(texture.Width);
            writer.Write(texture.Height);
            writer.Write(texture.Flags);
            writer.Write(0);
            writer.Write(0);
            writer.Write(texture.MipCount);
            foreach (var mip in texture.Mips) writer.Write(mip);
        }
        return stream.ToArray();
    }
}

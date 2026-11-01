using System.Buffers.Binary;
using System.Text;
using Sed.Formats.Gob;
using Sed.Formats.Material;
using Sed.Rendering;
using Xunit;

namespace Sed.Core.Tests;

/// <summary>16/32-bit MATs decode to RGBA8 from their declared tex_format, or OpenJKDF2's fixed 16-bit layouts.</summary>
public class TrueColorMatTests
{
    private sealed record Layout(int ColorMode, int Bpp, int RBits = 0, int GBits = 0, int BBits = 0,
        int RShift = 0, int GShift = 0, int BShift = 0, int ABits = 0, int AShift = 0);
    private sealed record Texture(int Width, int Height, int Flags, byte[][] Mips);

    private static readonly Layout Rgb565 = new(1, 16, 5, 6, 5, 11, 5, 0);
    // Con Krape's grs3.mat: RGB mode, 5/5/5 at 10/5/0, no declared alpha, alpha flag set.
    private static readonly Layout Rgb555 = new(1, 16, 5, 5, 5, 10, 5, 0);
    private static readonly Layout Argb8888 = new(2, 32, 8, 8, 8, 16, 8, 0, 8, 24);

    [Fact]
    public void Rgb565DecodesWithEngineRoundingAndLeavesIndicesEmpty()
    {
        var mat = MatFile.Read(Mat(Rgb565, 0, new Texture(2, 2, 0, [U16(0xF800, 0x07E0, 0x001F, 0x8410)])));
        Assert.True(mat.IsTrueColor);
        Assert.False(mat.IsColor);
        Assert.False(mat.HasColorKey);
        Assert.Equal(16, mat.BitsPerPixel);
        Assert.Empty(mat.Indices);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 132, 130, 132, 255 }, mat.Rgba);
        var copy = mat.ToRgba(Palette());
        Assert.Equal(mat.Rgba, copy);
        Assert.NotSame(mat.Rgba, copy);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 255)]
    public void MaskedFifteenBitMatKeepsItsMaskInBitFifteen(int flags, byte clearBitAlpha)
    {
        var mat = MatFile.Read(Mat(Rgb555, 0, new Texture(2, 1, flags, [U16(0x7C00, 0x83E0)])));
        Assert.Equal(flags == 1, mat.HasColorKey);
        Assert.Equal(new byte[] { 255, 0, 0, clearBitAlpha, 0, 255, 0, 255 }, mat.Rgba);
    }

    [Theory]
    [InlineData(0, new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 })]   // RGB565: 0xF800 red, 0x001F blue
    [InlineData(1, new byte[] { 255, 0, 0, 255, 0, 0, 255, 0 })]     // ARGB1555: 0xFC00 opaque red, 0x001F clear blue
    public void MissingChannelFieldsFollowOpenJkdf2AlphaFlagRule(int flags, byte[] expected)
    {
        ushort red = flags == 1 ? (ushort)0xFC00 : (ushort)0xF800;
        var mat = MatFile.Read(Mat(new Layout(1, 16), 0, new Texture(2, 1, flags, [U16(red, 0x001F)])));
        Assert.Equal(expected, mat.Rgba);
    }

    [Fact]
    public void ThirtyTwoBitDecodesDeclaredAlphaOrDefaultsOpaque()
    {
        var withAlpha = MatFile.Read(Mat(Argb8888, 0, new Texture(1, 1, 0, [U32(0x80112233)])));
        Assert.Equal(32, withAlpha.BitsPerPixel);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x80 }, withAlpha.Rgba);
        var opaque = MatFile.Read(Mat(Argb8888 with { ABits = 0, AShift = 0 }, 0, new Texture(1, 1, 0, [U32(0x00112233)])));
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0xFF }, opaque.Rgba);
    }

    [Fact]
    public void ThirtyTwoBitNeedsAValidNonOverlappingLayout()
    {
        // OpenJKDF2's fixed-layout fallback exists only for 16-bit MATs.
        var missing = Assert.Throws<InvalidDataException>(() => MatFile.Read(Mat(new Layout(2, 32), 0, new Texture(1, 1, 0, [U32(0)]))));
        Assert.Contains("no RGB channel layout", missing.Message);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(Mat(Argb8888 with { GShift = 12 }, 0, new Texture(1, 1, 0, [U32(0)]))));
        Assert.Throws<InvalidDataException>(() => MatFile.Read(Mat(Argb8888 with { AShift = 16 }, 0, new Texture(1, 1, 0, [U32(0)]))));
        Assert.Throws<InvalidDataException>(() => MatFile.Read(Mat(Argb8888 with { RBits = 9 }, 0, new Texture(1, 1, 0, [U32(0)]))));
    }

    [Theory]
    [InlineData(5, 6, 0, 11, 5, 0)]     // blue omitted: a partial layout, not a legacy omission
    [InlineData(5, 0, 0, 11, 0, 0)]     // only red declared
    [InlineData(5, 6, 5, 11, 8, 0)]     // green overlaps red
    [InlineData(5, 6, 5, 12, 5, 0)]     // red runs past bit 15
    [InlineData(-5, 6, 5, 11, 5, 0)]    // negative width
    [InlineData(9, 4, 3, 7, 3, 0)]      // a channel wider than eight bits
    public void SixteenBitDeclaredLayoutMustBeCompleteAndDisjoint(int rBits, int gBits, int bBits, int rShift, int gShift, int bShift)
    {
        var layout = new Layout(1, 16, rBits, gBits, bBits, rShift, gShift, bShift);
        foreach (int flags in new[] { 0, 1 })
        {
            var error = Assert.Throws<InvalidDataException>(() => MatFile.Read(Mat(layout, 0, new Texture(1, 1, flags, [U16(0)]))));
            Assert.Contains("invalid or overlapping RGB channel layout", error.Message);
        }
    }

    [Fact]
    public void DeclaredSixteenBitAlphaTakesPrecedenceOverTheMaskFlag()
    {
        // Explicit ARGB1555: bit 15 is alpha even on an unmasked texture.
        var argb1555 = new Layout(2, 16, 5, 5, 5, 10, 5, 0, 1, 15);
        var mat = MatFile.Read(Mat(argb1555, 0, new Texture(2, 1, 0, [U16(0xFC00, 0x03E0)])));
        Assert.False(mat.HasColorKey);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 0 }, mat.Rgba);
        // Explicit ARGB4444 on a masked texture keeps its four alpha bits, expanded (v*255+7)/15.
        var argb4444 = new Layout(2, 16, 4, 4, 4, 8, 4, 0, 4, 12);
        var four = MatFile.Read(Mat(argb4444, 0, new Texture(1, 1, 1, [U16(0x8F71)])));
        Assert.True(four.HasColorKey);
        Assert.Equal(new byte[] { 0xFF, 0x77, 0x11, 0x88 }, four.Rgba);
        // A masked RGB565 texture has no free bit for a mask, so it stays opaque.
        var masked565 = MatFile.Read(Mat(Rgb565, 0, new Texture(1, 1, 1, [U16(0xFFFF)])));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, masked565.Rgba);
    }

    [Fact]
    public void TrueColourMipChainsAreSizedAndCheckedAtTheirOwnDepth()
    {
        // 16-bit: two bytes per texel, so the 1x1 mip 1 needs two bytes. A complete base mip
        // with a truncated smaller mip shows mip 0 with a note (retail mip fallback).
        var sixteen = Mat(Rgb565, 0, new Texture(2, 2, 0, [U16(0xF800, 0x07E0, 0x001F, 0xFFFF), U16(0)]));
        byte[] baseMip = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255];
        var complete = MatFile.Read(sixteen);
        Assert.Equal(baseMip, complete.Rgba);
        Assert.Null(complete.MipFallbackNote);
        var shortMip = MatFile.Read(sixteen[..^1]);
        Assert.Equal(baseMip, shortMip.Rgba);
        Assert.Equal("Retail mip fallback: mip level 1 of 2 levels (1x1) needs 2 bytes but 1 remain; showing complete mip level 0, as retail JK does on faces nearer than the level's MipMap Distances; retail faces far enough to use mip level 1 or later show data the file never filled.",
            shortMip.MipFallbackNote);
        // 32-bit: four bytes per texel; one texel short is truncated.
        var thirtyTwo = Mat(Argb8888, 0, new Texture(2, 1, 0, [U32(0xFF102030, 0x80405060)]));
        var mat = MatFile.Read(thirtyTwo);
        Assert.Equal(32, mat.BitsPerPixel);
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0xFF, 0x40, 0x50, 0x60, 0x80 }, mat.Rgba);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(thirtyTwo[..^4]));
        Assert.Throws<InvalidDataException>(() => MatFile.Read(thirtyTwo[..^1]));
    }

    [Fact]
    public void FirstCelSkipsEarlierThirtyTwoBitMipChainsAtFourBytesPerPixel()
    {
        var first = new Texture(2, 2, 0, [U32(0x11111111, 0x22222222, 0x33333333, 0x44444444), U32(0x55555555)]);
        var second = new Texture(1, 1, 0, [U32(0xFF0000FF)]);
        var mat = MatFile.Read(Mat(Argb8888, 1, first, second));
        Assert.Equal(1, mat.Width);
        Assert.Equal(new byte[] { 0, 0, 0xFF, 0xFF }, mat.Rgba);
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(3, 16)]
    [InlineData(-1, 32)]
    [InlineData(1, 24)]
    [InlineData(2, 15)]
    public void TrueColourNeedsAnRgbModeAndSupportedDepth(int colorMode, int bpp) =>
        Assert.Throws<InvalidDataException>(() => MatFile.Read(Mat(Rgb565 with { ColorMode = colorMode, Bpp = bpp }, 0,
            new Texture(2, 1, 0, [new byte[2 * 4]]))));

    [Fact]
    public void FirstCelSkipsEarlierSixteenBitMipChainsAtTwoBytesPerPixel()
    {
        var first = new Texture(4, 4, 0, [U16(Enumerable.Repeat((ushort)0x07E0, 16).ToArray()), U16(0, 0, 0, 0), U16(0)]);
        var second = new Texture(2, 1, 0, [U16(0xF800, 0x001F)]);
        var bytes = Mat(Rgb565, 1, first, second);
        var mat = MatFile.Read(bytes);
        Assert.Equal(2, mat.Width);
        Assert.Equal(1, mat.Height);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, mat.Rgba);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes[..^1]));
        // A file sized for one byte per pixel cannot reach the second texture.
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes[..(116 + 24 + 21 + 24 + 2)]));
    }

    [Fact]
    public void IndexedMatsStillIgnoreUninitialisedChannelFields()
    {
        // Real 8-bit MATs (kelltest km_blade.mat, Mos Espa dflt.mat) carry garbage after bpp.
        var mat = MatFile.Read(Mat(new Layout(0, 8, 9961472, 64, 9961484, 2688, 0, 64, 9961472, 64), 0,
            new Texture(2, 1, 0, [[3, 4]])));
        Assert.False(mat.IsTrueColor);
        Assert.Equal(new byte[] { 3, 4 }, mat.Indices);
        Assert.Empty(mat.Rgba);
    }

    [Fact]
    public void LibraryAndRenderTextureCarryTrueColour()
    {
        using var archive = Archive(("mat/grass.mat", Mat(Rgb555, 0, new Texture(2, 1, 1, [U16(0x7C00, 0x83E0)]))),
            ("mat/plain.mat", Mat(new Layout(0, 8), 0, new Texture(2, 1, 0, [[0, 7]]))),
            ("mat/broken.mat", Mat(new Layout(1, 16, 5, 6, 0, 11, 5, 0), 0, new Texture(1, 1, 0, [U16(0)]))));
        var library = new MaterialLibrary(Palette(), archive);
        var grass = library.GetIndexed("grass.mat");
        Assert.True(grass.HasValue);
        Assert.True(grass.Value.HasColorKey);
        Assert.False(grass.Value.IsSolid);
        Assert.Empty(grass.Value.Indices);
        Assert.Equal(new byte[] { 255, 0, 0, 0, 0, 255, 0, 255 }, grass.Value.Rgba);
        // The RGBA view is the decoded texture itself; the CMP plays no part.
        Assert.Equal(grass.Value.Rgba, library.Get("grass.mat")!.Value.Rgba);
        var plain = library.GetIndexed("plain.mat")!.Value;
        Assert.Null(plain.Rgba);
        Assert.Equal(new byte[] { 0, 7 }, plain.Indices);
        // A damaged true-colour MAT is unreadable, exactly like a damaged indexed one.
        Assert.Null(library.GetIndexed("broken.mat"));
        Assert.Null(library.Get("broken.mat"));

        var texture = new IndexedTexture(grass.Value.Width, grass.Value.Height, grass.Value.Indices, grass.Value.IsSolid,
            grass.Value.HasColorKey, grass.Value.Rgba);
        Assert.True(texture.IsTrueColor);
        Assert.False(new IndexedTexture(1, 1, [1]).IsTrueColor);
    }

    private static byte[] Mat(Layout layout, int selected, params Texture[] textures)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("MAT "));
        writer.Write(0x32);
        writer.Write(2);
        writer.Write(1);
        writer.Write(textures.Length);
        writer.Write(layout.ColorMode);
        writer.Write(layout.Bpp);
        writer.Write(layout.RBits); writer.Write(layout.GBits); writer.Write(layout.BBits);
        writer.Write(layout.RShift); writer.Write(layout.GShift); writer.Write(layout.BShift);
        writer.Write(8 - layout.RBits); writer.Write(8 - layout.GBits); writer.Write(8 - layout.BBits);
        writer.Write(layout.ABits); writer.Write(layout.AShift); writer.Write(8 - layout.ABits);
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
            writer.Write(texture.Mips.Length);
            foreach (var mip in texture.Mips) writer.Write(mip);
        }
        return stream.ToArray();
    }

    private static byte[] U16(params ushort[] values)
    {
        var bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    private static byte[] U32(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static Colormap Palette()
    {
        var bytes = new byte[64 + 768];
        Encoding.ASCII.GetBytes("CMP ").CopyTo(bytes, 0);
        new byte[] { 10, 80, 240 }.CopyTo(bytes, 64 + 7 * 3);
        return Colormap.Read(bytes);
    }

    private static GobArchive Archive(params (string Name, byte[] Bytes)[] entries)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("GOB "));
        writer.Write(0x14);
        writer.Write(12);
        writer.Write(entries.Length);
        int offset = 16 + entries.Length * 136;
        foreach (var (name, bytes) in entries)
        {
            writer.Write(offset);
            writer.Write(bytes.Length);
            var nameBytes = new byte[128];
            Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
            writer.Write(nameBytes);
            offset += bytes.Length;
        }
        foreach (var (_, bytes) in entries) writer.Write(bytes);
        return new GobArchive(stream, ownsStream: true);
    }
}

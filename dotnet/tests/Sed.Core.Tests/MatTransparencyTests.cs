using System.Buffers.Binary;
using System.Text;
using Sed.Formats.Gob;
using Sed.Formats.Material;
using Xunit;

namespace Sed.Core.Tests;

public class MatTransparencyTests
{
    private sealed record Texture(int Width, int Height, int Flags, byte[][] Mips, int StoredKey = 0);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(-1, true)]
    public void OnlyLowAlphaFlagBitControlsIndexedZero(int flags, bool masked)
    {
        var mat = MatFile.Read(Mat(0, new Texture(2, 1, flags, [[0, 7]], StoredKey: 7)));
        Assert.Equal(masked, mat.HasColorKey);
        var rgba = mat.ToRgba(Palette());
        Assert.Equal(new byte[] { 200, 40, 90, masked ? (byte)0 : (byte)255, 10, 80, 240, 255 }, rgba);
        Assert.Equal(new byte[] { 0, 7 }, mat.Indices);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void FirstCelSelectsTextureAndMaskAfterAllEarlierMips(int firstFlags, int secondFlags)
    {
        var first = new Texture(4, 4, firstFlags,
            [Enumerable.Repeat((byte)77, 16).ToArray(), [33, 33, 33, 33], [99]]);
        var second = new Texture(2, 1, secondFlags, [[0, 7]]);
        // One cel, two textures: the cel count must not substitute for texture count.
        var mat = MatFile.Read(Mat(1, first, second));
        Assert.Equal(2, mat.Width);
        Assert.Equal(1, mat.Height);
        Assert.Equal(new byte[] { 0, 7 }, mat.Indices);
        Assert.Equal((secondFlags & 1) != 0, mat.HasColorKey);
    }

    [Fact]
    public void FirstCelCanReuseTextureZeroWhenThereAreSeveralCels()
    {
        var source = Mat(0, new Texture(2, 1, 1, [[0, 7]]));
        // Add a second cel pointing at the same sole texture, before its data header.
        var bytes = new byte[source.Length + 40];
        source.AsSpan(0, 116).CopyTo(bytes);
        source.AsSpan(76, 40).CopyTo(bytes.AsSpan(116));
        source.AsSpan(116).CopyTo(bytes.AsSpan(156));
        Set(bytes, 12, 2);
        var mat = MatFile.Read(bytes);
        Assert.Equal(new byte[] { 0, 7 }, mat.Indices);
        Assert.True(mat.HasColorKey);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void FirstCelCannotReferenceMissingTexture(int selected)
    {
        var bytes = Mat(selected, new Texture(2, 1, 0, [[0, 7]]));
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void DeclaredTextureCountIsBounded(int count)
    {
        var bytes = Mat(0, new Texture(2, 1, 0, [[0, 7]]));
        Set(bytes, 16, count);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void MipCountMustFitTextureDimensions(int mipCount)
    {
        var bytes = Mat(0, new Texture(2, 2, 0, [[0, 7, 7, 0], [7]]));
        Set(bytes, 116 + 20, mipCount);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes));
    }

    [Fact]
    public void TruncatedSelectedMipChainCannotSucceedFromBaseMipAlone()
    {
        var bytes = Mat(0, new Texture(2, 2, 1, [[0, 7, 7, 0], [7]]));
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes[..^1]));
    }

    [Fact]
    public void TruncatedEarlierMipChainCannotSelectFollowingTexture()
    {
        var bytes = Mat(1,
            new Texture(4, 4, 0, [new byte[16], new byte[4], [0]]),
            new Texture(2, 1, 1, [[0, 7]]));
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes[..(116 + 24 + 19)]));
    }

    [Fact]
    public void LibraryCarriesMaskAndKeepsColorZeroOpaque()
    {
        using var archive = Archive(
            ("mat/masked.mat", Mat(0, new Texture(2, 1, 1, [[0, 7]]))),
            ("mat/opaque.mat", Mat(0, new Texture(2, 1, 0, [[0, 7]]))),
            ("mat/solid.mat", ColorMat(0)));
        var library = new MaterialLibrary(Palette(), archive);
        var masked = library.GetIndexed("masked.mat");
        var opaque = library.GetIndexed("opaque.mat");
        var solid = library.GetIndexed("solid.mat");
        Assert.True(masked.HasValue);
        Assert.True(masked.Value.HasColorKey);
        Assert.False(masked.Value.IsSolid);
        Assert.True(opaque.HasValue);
        Assert.False(opaque.Value.HasColorKey);
        Assert.True(solid.HasValue);
        Assert.True(solid.Value.IsSolid);
        Assert.False(solid.Value.HasColorKey);
        Assert.Equal(new byte[] { 0 }, solid.Value.Indices);
        Assert.Equal((byte)0, library.Get("masked.mat")!.Value.Rgba[3]);
        Assert.Equal((byte)255, library.Get("opaque.mat")!.Value.Rgba[3]);
        Assert.Equal((byte)255, library.Get("solid.mat")!.Value.Rgba[3]);
        Assert.Equal(masked, library.GetIndexed("masked.mat"));
        Assert.Equal(opaque, library.GetIndexed("opaque.mat"));
    }

    private static byte[] Mat(int selected, params Texture[] textures)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("MAT "));
        writer.Write(0x32);
        writer.Write(2);
        writer.Write(1);
        writer.Write(textures.Length);
        writer.Write(0);
        writer.Write(8);
        writer.Write(new byte[48]);
        writer.Write(8); // Textured cel; engine consumes its 16-byte extension.
        writer.Write(new byte[32]);
        writer.Write(selected);
        foreach (var texture in textures)
        {
            writer.Write(texture.Width);
            writer.Write(texture.Height);
            writer.Write(texture.Flags);
            writer.Write(0);
            writer.Write(texture.StoredKey);
            writer.Write(texture.Mips.Length);
            foreach (var mip in texture.Mips) writer.Write(mip);
        }
        return stream.ToArray();
    }

    private static byte[] ColorMat(int index)
    {
        var bytes = new byte[100];
        Encoding.ASCII.GetBytes("MAT ").CopyTo(bytes, 0);
        Set(bytes, 4, 0x32);
        Set(bytes, 12, 1);
        Set(bytes, 80, index);
        return bytes;
    }

    private static Colormap Palette()
    {
        var bytes = new byte[64 + 768];
        Encoding.ASCII.GetBytes("CMP ").CopyTo(bytes, 0);
        new byte[] { 200, 40, 90 }.CopyTo(bytes, 64);
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

    private static void Set(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);
}

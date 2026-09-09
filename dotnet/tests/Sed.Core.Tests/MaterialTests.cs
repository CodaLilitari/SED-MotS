using System.Text;
using Sed.Formats.Material;
using Xunit;

namespace Sed.Core.Tests;

public class MaterialTests
{
    private static byte[] BuildCmp()
    {
        var d = new byte[64 + 256 * 3];
        Encoding.ASCII.GetBytes("CMP ").CopyTo(d, 0);
        // index 5 -> (10,20,30)
        int p = 64 + 5 * 3;
        d[p] = 10; d[p + 1] = 20; d[p + 2] = 30;
        return d;
    }

    private static byte[] BuildTextureMat(int w, int h, byte[] indices)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(Encoding.ASCII.GetBytes("MAT "));
        bw.Write(0x32);          // version
        bw.Write(2);             // type = texture
        bw.Write(1);             // celCount
        bw.Write(1);             // textureCount
        // ColorInfo (56 bytes): mode, bpp=8, then 12 more uint32 (zero)
        bw.Write(0);             // mode
        bw.Write(8);             // bpp  (offset 24)
        for (int i = 0; i < 12; i++) bw.Write(0);
        // one TextureHeader (40 bytes)
        bw.Write(new byte[40]);
        // TextureData: w, h, 3 pad, numMips
        bw.Write(w); bw.Write(h); bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(1);
        bw.Write(indices);
        return ms.ToArray();
    }

    [Fact]
    public void Colormap_DecodesPalette_AndTreatsIndexZeroTransparent()
    {
        var cmp = Colormap.Read(BuildCmp());
        Assert.Equal((10, 20, 30, 255), cmp[5]);
        Assert.Equal((byte)0, cmp[0].A);
    }

    [Fact]
    public void MatFile_DecodesTextureDimensionsAndIndices()
    {
        var mat = MatFile.Read(BuildTextureMat(2, 2, new byte[] { 5, 5, 5, 5 }));
        Assert.False(mat.IsColor);
        Assert.Equal(2, mat.Width);
        Assert.Equal(2, mat.Height);
        Assert.Equal(4, mat.Indices.Length);
    }

    [Fact]
    public void MatFile_ToRgba_ResolvesThroughPalette()
    {
        var cmp = Colormap.Read(BuildCmp());
        var mat = MatFile.Read(BuildTextureMat(2, 2, new byte[] { 5, 5, 5, 5 }));
        var rgba = mat.ToRgba(cmp);
        Assert.Equal(2 * 2 * 4, rgba.Length);
        Assert.Equal(10, rgba[0]);   // R of index 5
        Assert.Equal(20, rgba[1]);
        Assert.Equal(30, rgba[2]);
    }

    [Theory]
    [InlineData(16)] [InlineData(24)] [InlineData(32)] [InlineData(9)]
    public void TruecolorPixelsCannotBeMistakenForPaletteIndices(int bpp)
    {
        var bytes = BuildTextureMat(2, 2, new byte[16]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), bpp);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes));
    }

    [Fact]
    public void TextureDimensionsCelHeadersAndTruncatedColorAreBounded()
    {
        var bytes = BuildTextureMat(int.MaxValue, 2, new byte[4]);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes));
        bytes = BuildTextureMat(2, 2, new byte[4]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes));
        bytes = BuildTextureMat(2, 2, new byte[4])[..76];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 0);
        Assert.Throws<InvalidDataException>(() => MatFile.Read(bytes));
    }
}

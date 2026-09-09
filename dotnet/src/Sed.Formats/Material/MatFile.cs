using System.Buffers.Binary;

namespace Sed.Formats.Material;

/// <summary>
/// A decoded JK MAT material — either a flat color or an 8-bit indexed texture
/// (first/largest cel only). Indices reference a <see cref="Colormap"/> palette.
///
/// Layout (per graph_files.pas):
///   header 76 bytes: "MAT ", int version(0x32), int type(0=color,2=texture),
///   int celCount, int textureCount, ColorInfo(56 — bpp at offset 24).
///   Color MAT: ColorHeader(textype,int colornum,…). Texture MAT: celCount ×
///   TextureHeader(40) then per cel TextureData(int w, int h, 3×int pad,
///   int numMips) followed by mip pixel data (largest first).
/// </summary>
public sealed class MatFile
{
    private const int HeaderSize = 76;
    private const int TextureHeaderSize = 40;
    private const int TextureDataHeader = 24;

    public int Width { get; private init; }
    public int Height { get; private init; }
    public bool IsColor { get; private init; }
    public int ColorIndex { get; private init; }
    public byte[] Indices { get; private init; } = Array.Empty<byte>();

    public static MatFile Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize || d[0] != (byte)'M' || d[1] != (byte)'A' || d[2] != (byte)'T')
            throw new InvalidDataException("Not a MAT file (bad magic)");

        int version = I32(d, 4);
        if (version != 0x32)
            throw new InvalidDataException($"Unsupported MAT version 0x{version:X}");

        int type = I32(d, 8);
        int celCount = I32(d, 12);
        int bpp = I32(d, 24);            // ColorInfo.bpp

        if (type == 0)
        {
            if (d.Length < HeaderSize + 8) throw new InvalidDataException("Truncated color MAT header.");
            // Flat color: ColorHeader { int textype, int colornum, … } at offset 76.
            int colorNum = I32(d, HeaderSize + 4);
            return new MatFile { Width = 64, Height = 64, IsColor = true, ColorIndex = colorNum };
        }

        if (type != 2) throw new InvalidDataException($"Unsupported MAT type {type}.");
        if (bpp != 8)
            throw new InvalidDataException($"MAT {bpp}-bit pixels are not supported by the indexed decoder.");
        if (celCount < 1 || celCount > (d.Length - HeaderSize) / TextureHeaderSize)
            throw new InvalidDataException("MAT cel headers are out of range.");

        // Skip the per-cel texture headers, then read cel 0's TextureData.
        int td = HeaderSize + celCount * TextureHeaderSize;
        if (td > d.Length - TextureDataHeader) throw new InvalidDataException("Truncated MAT texture header.");
        int w = I32(d, td);
        int h = I32(d, td + 4);
        int pixels = td + TextureDataHeader;
        if (w < 1 || h < 1 || w > 16384 || h > 16384 || (long)w * h > 16_777_216)
            throw new InvalidDataException("MAT dimensions exceed the supported 16-million-pixel budget.");
        int count = w * h;
        if (count > d.Length - pixels)
            throw new InvalidDataException("MAT pixel data out of range");

        var indices = d.Slice(pixels, count).ToArray();
        return new MatFile { Width = w, Height = h, Indices = indices };
    }

    /// <summary>Resolves this material to tightly-packed RGBA8 using the given palette.</summary>
    public byte[] ToRgba(Colormap palette)
    {
        var rgba = new byte[Width * Height * 4];
        if (IsColor)
        {
            var (r, g, b, _) = palette[ColorIndex];
            for (int i = 0; i < Width * Height; i++)
            {
                rgba[i * 4 + 0] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255;
            }
            return rgba;
        }

        for (int i = 0; i < Indices.Length; i++)
        {
            var (r, g, b, a) = palette[Indices[i]];
            rgba[i * 4 + 0] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
        }
        return rgba;
    }

    private static int I32(ReadOnlySpan<byte> d, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(d.Slice(offset, 4));
}

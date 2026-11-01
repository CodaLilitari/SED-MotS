using System.Buffers.Binary;

namespace Sed.Formats.Material;

/// <summary>
/// A decoded JK MAT material: a flat color, an 8-bit indexed texture, or a
/// 16/32-bit true-colour texture (largest mip of the first cel only). Indices
/// reference a <see cref="Colormap"/> palette; true-colour pixels are decoded
/// to tightly-packed RGBA8 in <see cref="Rgba"/> and leave <see cref="Indices"/> empty.
///
/// Layout (per graph_files.pas and OpenJKDF2 rdMaterial_LoadEntry):
///   header 76 bytes: "MAT ", int version(0x32), int type(0=color,2=texture),
///   int celCount, int textureCount, tex_format(56): colorMode@20 (0 indexed,
///   1 RGB, 2 RGBA), bpp@24, r/g/b bits@28/32/36, r/g/b shifts@40/44/48,
///   r/g/b bit differences@52/56/60, alpha bits/shift/difference@64/68/72.
///   Color MAT: ColorHeader(textype,int colornum,…). Texture MAT: celCount ×
///   TextureHeader(40) then per texture TextureData(int w, int h, int alphaFlags,
///   int unknown, int storedColorKey, int numMips) followed by mip pixel data of
///   w*h*bpp/8 bytes per mip. Each texture cel references a texture ID at
///   cel-header offset 36; texture and cel counts need not match. Mixed
///   solid/textured cel tables are not decoded.
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
    /// <summary>Texture alpha flag bit 0; the accelerated indexed path keys index zero.</summary>
    public bool HasColorKey { get; private init; }
    /// <summary>8-bit palette indices; empty for true-colour and flat-color MATs.</summary>
    public byte[] Indices { get; private init; } = Array.Empty<byte>();
    /// <summary>Stored bits per pixel of a texture MAT (8, 16 or 32).</summary>
    public int BitsPerPixel { get; private init; } = 8;
    /// <summary>Decoded RGBA8 for a 16/32-bit texture; empty for indexed and flat-color MATs.</summary>
    public byte[] Rgba { get; private init; } = Array.Empty<byte>();
    /// <summary>True when the texture stores 16 or 32-bit colour rather than palette indices.</summary>
    public bool IsTrueColor => Rgba.Length != 0;

    public static MatFile Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < HeaderSize || d[0] != (byte)'M' || d[1] != (byte)'A' || d[2] != (byte)'T')
            throw new InvalidDataException("Not a MAT file (bad magic)");

        int version = I32(d, 4);
        if (version != 0x32)
            throw new InvalidDataException($"Unsupported MAT version 0x{version:X}");

        int type = I32(d, 8);
        int celCount = I32(d, 12);
        int colorMode = I32(d, 20);      // tex_format.colorMode (OpenJKDF2 is16bit)
        int bpp = I32(d, 24);            // tex_format.bpp

        if (type == 0)
        {
            if (d.Length < HeaderSize + 8) throw new InvalidDataException("Truncated color MAT header.");
            // Flat color: ColorHeader { int textype, int colornum, … } at offset 76.
            int colorNum = I32(d, HeaderSize + 4);
            return new MatFile { Width = 64, Height = 64, IsColor = true, ColorIndex = colorNum };
        }

        if (type != 2) throw new InvalidDataException($"Unsupported MAT type {type}.");
        // Indexed MATs often carry uninitialised channel fields; only bpp 8 is
        // consulted for them. True colour must declare an RGB/RGBA colour mode so
        // stored pixels are never mistaken for palette indices (or vice versa).
        if (bpp is 16 or 32)
        {
            if (colorMode is not (1 or 2))
                throw new InvalidDataException($"MAT {bpp}-bit pixels require an RGB or RGBA colour mode, not mode {colorMode}.");
        }
        else if (bpp != 8)
            throw new InvalidDataException($"MAT {bpp}-bit pixels are not supported; expected 8, 16 or 32.");
        int bytesPerPixel = bpp / 8;
        if (celCount < 1 || celCount > (d.Length - HeaderSize) / TextureHeaderSize)
            throw new InvalidDataException("MAT cel headers are out of range.");

        // This decoder retains the existing all-textured 40-byte cel table layout.
        // The first cel may reference a later texture, whose header follows all
        // preceding textures' complete mip chains (rdMaterial_LoadEntry).
        int td = HeaderSize + celCount * TextureHeaderSize;
        int textureCount = I32(d, 16);
        if (textureCount < 1 || textureCount > (d.Length - td) / TextureDataHeader)
            throw new InvalidDataException("MAT texture count is out of range.");
        int selectedTexture = I32(d, HeaderSize + 36);
        if (selectedTexture < 0 || selectedTexture >= textureCount)
            throw new InvalidDataException("MAT first cel references an out-of-range texture.");

        for (int texture = 0; texture <= selectedTexture; texture++)
        {
            if (td > d.Length - TextureDataHeader)
                throw new InvalidDataException("Truncated MAT texture header.");
            int w = I32(d, td), h = I32(d, td + 4);
            if (w < 1 || h < 1 || w > 16384 || h > 16384 || (long)w * h > 16_777_216)
                throw new InvalidDataException("MAT dimensions exceed the supported 16-million-pixel budget.");
            int alphaFlags = I32(d, td + 8);
            bool hasColorKey = (alphaFlags & 1) != 0;
            int mipCount = I32(d, td + 20);
            if (mipCount < 1 || mipCount > 15)
                throw new InvalidDataException("MAT mip count is out of range.");
            int pixels = td + TextureDataHeader;
            td = pixels;
            int mipWidth = w, mipHeight = h;
            for (int mip = 0; mip < mipCount; mip++)
            {
                if (mipWidth == 0 || mipHeight == 0)
                    throw new InvalidDataException("MAT mip count exceeds its dimensions.");
                int mipBytes = mipWidth * mipHeight * bytesPerPixel;
                if (mipBytes > d.Length - td)
                    throw new InvalidDataException("Truncated MAT mip pixel data.");
                td += mipBytes;
                mipWidth >>= 1;
                mipHeight >>= 1;
            }
            if (texture != selectedTexture) continue;
            if (bpp == 8)
                return new MatFile
                {
                    Width = w, Height = h, HasColorKey = hasColorKey,
                    Indices = d.Slice(pixels, w * h).ToArray()
                };
            var layout = TrueColorLayout.From(d, bpp, hasColorKey);
            return new MatFile
            {
                Width = w, Height = h, HasColorKey = hasColorKey, BitsPerPixel = bpp,
                Rgba = layout.Decode(d.Slice(pixels, w * h * bytesPerPixel), w * h)
            };
        }
        throw new InvalidDataException("MAT selected texture was not found.");
    }

    /// <summary>Resolves this material to tightly-packed RGBA8 using the given palette.</summary>
    public byte[] ToRgba(Colormap palette)
    {
        // True colour needs no palette; return a copy so callers cannot alter the MAT.
        if (IsTrueColor) return (byte[])Rgba.Clone();
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
            var (r, g, b, _) = palette[Indices[i]];
            // CMP itself has no per-material transparency. Its convenience RGBA
            // view's zero alpha must not make an unmasked texture's zero disappear.
            byte a = (byte)(HasColorKey && Indices[i] == 0 ? 0 : 255);
            rgba[i * 4 + 0] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
        }
        return rgba;
    }

    private static int I32(ReadOnlySpan<byte> d, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(d.Slice(offset, 4));

    /// <summary>One packed channel: <c>Bits</c> wide at <c>Shift</c>; zero bits means absent.</summary>
    private readonly record struct Channel(int Bits, int Shift)
    {
        public bool IsValid(int bpp) => Bits is >= 1 and <= 8 && Shift >= 0 && Shift <= bpp - Bits;
        public uint Mask => Bits == 0 ? 0u : ((1u << Bits) - 1) << Shift;

        /// <summary>Expands to eight bits with rounding, (v*255+max/2)/max, as GL does for packed formats.</summary>
        public byte Expand(uint pixel, byte absent)
        {
            if (Bits == 0) return absent;
            uint max = (1u << Bits) - 1;
            uint value = (pixel >> Shift) & max;
            return (byte)((value * 255 + max / 2) / max);
        }
    }

    /// <summary>Packed true-colour channel layout from tex_format, or OpenJKDF2's fixed 16-bit layouts.</summary>
    private readonly record struct TrueColorLayout(int Bpp, Channel R, Channel G, Channel B, Channel A)
    {
        public static TrueColorLayout From(ReadOnlySpan<byte> d, int bpp, bool alphaEnabled)
        {
            var r = new Channel(I32(d, 28), I32(d, 40));
            var g = new Channel(I32(d, 32), I32(d, 44));
            var b = new Channel(I32(d, 36), I32(d, 48));
            var a = new Channel(I32(d, 64), I32(d, 68));
            if (r.Bits == 0 && g.Bits == 0 && b.Bits == 0)
            {
                // Legacy JKPaint 16-bit MATs omit the channel fields. OpenJKDF2 never
                // reads them: std3D_AddToTextureCache uploads 16-bit pixels as
                // 5_6_5_REV, or 1_5_5_5_REV when alpha_en & 1, and default_f.glsl
                // swaps red and blue, giving standard RGB565 or ARGB1555.
                if (bpp != 16)
                    throw new InvalidDataException($"MAT {bpp}-bit pixels declare no RGB channel layout.");
                return alphaEnabled
                    ? new(16, new(5, 10), new(5, 5), new(5, 0), new(1, 15))
                    : new(16, new(5, 11), new(6, 5), new(5, 0), default);
            }
            // A declared layout must be complete: a partial or overlapping one is damage, not a legacy omission.
            if (!r.IsValid(bpp) || !g.IsValid(bpp) || !b.IsValid(bpp)
                || (r.Mask & g.Mask) != 0 || (r.Mask & b.Mask) != 0 || (g.Mask & b.Mask) != 0)
                throw new InvalidDataException($"MAT {bpp}-bit pixels declare an invalid or overlapping RGB channel layout.");
            uint rgbMask = r.Mask | g.Mask | b.Mask;
            if (a.Bits != 0)
            {
                if (!a.IsValid(bpp) || (a.Mask & rgbMask) != 0)
                    throw new InvalidDataException($"MAT {bpp}-bit pixels declare an invalid or overlapping alpha channel.");
            }
            else if (bpp == 16 && alphaEnabled && (rgbMask & 0x8000u) == 0)
            {
                // No declared alpha, but a masked texture: OpenJKDF2 uploads every
                // masked 16-bit texture as ARGB1555, so a free bit 15 is its mask.
                // Con Krape's grs3.mat (5/5/5 at 10/5/0, alpha bits 0, alpha flag 1)
                // sets bit 15 on all 5,014 non-zero texels and on none of its 60,522
                // 0x0000 texels. Declared alpha bits take precedence over this rule.
                a = new Channel(1, 15);
            }
            return new(bpp, r, g, b, a);
        }

        public byte[] Decode(ReadOnlySpan<byte> source, int pixelCount)
        {
            var rgba = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount; i++)
            {
                uint pixel = Bpp == 16
                    ? BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(i * 2, 2))
                    : BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(i * 4, 4));
                rgba[i * 4 + 0] = R.Expand(pixel, 0);
                rgba[i * 4 + 1] = G.Expand(pixel, 0);
                rgba[i * 4 + 2] = B.Expand(pixel, 0);
                rgba[i * 4 + 3] = A.Expand(pixel, 255);
            }
            return rgba;
        }
    }
}

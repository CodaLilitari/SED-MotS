using System.Text;

namespace Sed.Formats.Material;

/// <summary>
/// A JK CMP colormap. Decodes the 256-entry RGB palette and 64-row lighting
/// table. The separate software transparency table is not decoded here.
///
/// Layout (per graph_files.pas): 64-byte header (sig "CMP ", uint32 ×2, 52 pad
/// bytes) then 256 × { byte r, g, b }. Palette values are full 0–255 range.
/// </summary>
public sealed class Colormap
{
    private const int HeaderSize = 64;
    private const int PaletteEntries = 256;
    private const int PaletteBytes = PaletteEntries * 3;
    public const int LightLevels = 64;
    private const int LightTableBytes = LightLevels * PaletteEntries;

    /// <summary>256 entries of packed RGBA (a=255), index 0 is treated as transparent.</summary>
    public byte[] Rgba { get; } = new byte[PaletteEntries * 4];

    /// <summary>Raw 256×3 RGB palette (for an indexed-rendering palette texture).</summary>
    public byte[] PaletteRgb { get; } = new byte[PaletteBytes];

    /// <summary>
    /// 64×256 light ramp: <c>LightTable[level*256 + index]</c> is the palette index
    /// to use for source <c>index</c> at light <c>level</c> (63 = full bright).
    /// Identity ramp if the CMP omits it (smaller files).
    /// </summary>
    public byte[] LightTable { get; } = new byte[LightTableBytes];

    public static Colormap Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != (byte)'C' || data[1] != (byte)'M' || data[2] != (byte)'P')
            throw new InvalidDataException("Not a CMP colormap (bad magic)");
        if (data.Length < HeaderSize + PaletteBytes)
            throw new InvalidDataException("CMP too small for a palette");

        var cmp = new Colormap();
        data.Slice(HeaderSize, PaletteBytes).CopyTo(cmp.PaletteRgb);
        for (int i = 0; i < PaletteEntries; i++)
        {
            cmp.Rgba[i * 4 + 0] = cmp.PaletteRgb[i * 3 + 0];
            cmp.Rgba[i * 4 + 1] = cmp.PaletteRgb[i * 3 + 1];
            cmp.Rgba[i * 4 + 2] = cmp.PaletteRgb[i * 3 + 2];
            cmp.Rgba[i * 4 + 3] = (byte)(i == 0 ? 0 : 255);
        }

        int lightOffset = HeaderSize + PaletteBytes;
        if (data.Length >= lightOffset + LightTableBytes)
            data.Slice(lightOffset, LightTableBytes).CopyTo(cmp.LightTable);
        else
            for (int level = 0; level < LightLevels; level++)
                for (int i = 0; i < PaletteEntries; i++)
                    cmp.LightTable[level * PaletteEntries + i] = (byte)i; // identity
        return cmp;
    }

    public (byte R, byte G, byte B, byte A) this[int index]
    {
        get
        {
            int i = (index & 0xFF) * 4;
            return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
        }
    }
}

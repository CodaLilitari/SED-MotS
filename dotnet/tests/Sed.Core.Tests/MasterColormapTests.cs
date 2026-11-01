using System.Text;
using Sed.Formats.Gob;
using Sed.Rendering;

namespace Sed.Core.Tests;

/// <summary>JK displays every indexed texture through World Colormap 0; sector CMPs supply ramps (and solid-colour palettes).</summary>
public sealed class MasterColormapTests
{
    private static ColormapData Cmp(byte marker)
    {
        var palette = new byte[ColormapData.PaletteBytes];
        palette[3] = marker;
        return new ColormapData(palette, new byte[ColormapData.LightTableBytes]);
    }

    private static (ColormapLookup Lookup, List<string> Requests) Archive(params (string Name, byte Marker)[] entries)
    {
        var requests = new List<string>();
        ColormapData? Lookup(string name)
        {
            requests.Add(name);
            foreach (var (entry, marker) in entries)
                if (entry.Equals(name, StringComparison.OrdinalIgnoreCase)) return Cmp(marker);
            return null;
        }
        return (Lookup, requests);
    }

    [Fact]
    public void FirstWorldColormapIsThePaletteEvenWhenLaterOnesAndDefaultExist()
    {
        var (lookup, requests) = Archive(("level.cmp", 10), ("sector.cmp", 20), ("dflt.cmp", 30));
        var result = MasterColormap.Resolve(["level.cmp", "sector.cmp"], lookup);
        Assert.Equal("level.cmp", result.Name);
        Assert.Equal(10, result.Colormap!.Value.PaletteRgb[3]);
        Assert.Empty(result.Problems);
        Assert.Equal(new[] { "level.cmp" }, requests);
    }

    [Fact]
    public void UnavailableFirstColormapFallsBackToDefaultNotToALaterSectorColormap()
    {
        var (lookup, requests) = Archive(("sector.cmp", 20), ("dflt.cmp", 30));
        var result = MasterColormap.Resolve(["custom.cmp", "sector.cmp"], lookup);
        Assert.Equal("dflt.cmp", result.Name);
        Assert.Equal(30, result.Colormap!.Value.PaletteRgb[3]);
        Assert.Equal(new[] { "custom.cmp", "dflt.cmp" }, requests);
        var problem = Assert.Single(result.Problems);
        Assert.Equal("Master colormap 'custom.cmp' (World Colormap 0) is unavailable; using dflt.cmp's palette instead.", problem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LevelWithoutAWorldColormapUsesDefaultQuietly(bool blankEntry)
    {
        string[] colormaps = blankEntry ? [" "] : [];
        var (lookup, requests) = Archive(("dflt.cmp", 30));
        var result = MasterColormap.Resolve(colormaps, lookup);
        Assert.Equal("dflt.cmp", result.Name);
        Assert.Equal(30, result.Colormap!.Value.PaletteRgb[3]);
        Assert.Empty(result.Problems);
        Assert.Equal(new[] { "dflt.cmp" }, requests);
    }

    [Fact]
    public void NoAvailablePaletteIsStatedRatherThanInvented()
    {
        var (lookup, requests) = Archive(("sector.cmp", 20));
        var result = MasterColormap.Resolve(["custom.cmp", "sector.cmp"], lookup);
        Assert.Null(result.Name);
        Assert.Null(result.Colormap);
        // One accurate statement: it does not claim the dflt.cmp fallback it could not take.
        Assert.Equal("No master palette is available: World Colormap 0 'custom.cmp' and dflt.cmp are both unavailable.",
            Assert.Single(result.Problems));
        Assert.Equal(new[] { "custom.cmp", "dflt.cmp" }, requests);

        // A missing listed dflt.cmp is not looked up twice.
        var (onlyDefault, defaultRequests) = Archive();
        var missingDefault = MasterColormap.Resolve(["DFLT.CMP"], onlyDefault);
        Assert.Null(missingDefault.Colormap);
        Assert.Equal("No master palette is available: dflt.cmp is unavailable.", Assert.Single(missingDefault.Problems));
        Assert.Equal(new[] { "DFLT.CMP" }, defaultRequests);
    }

    // The editor and probe tools resolve from GOBs. This is the reviewed failing input: World
    // Colormaps ["custom.cmp", "sector.cmp"], with custom.cmp in no archive. The old editor rule
    // (first listed CMP found, then dflt.cmp) displayed the level through sector.cmp's palette.
    [Fact]
    public void ArchiveResolutionFallsBackToDefaultNotToALaterSectorColormap()
    {
        using var resources = Gob(("misc/cmp/sector.cmp", CmpFile(20)), ("misc/cmp/dflt.cmp", CmpFile(30)));
        var (result, palette) = MasterColormap.ResolveFromArchives(["custom.cmp", "sector.cmp"], [resources]);
        Assert.Equal("dflt.cmp", result.Name);
        Assert.Equal(30, palette!.PaletteRgb[3]);
        Assert.Equal(30, result.Colormap!.Value.PaletteRgb[3]);
        Assert.Equal("Master colormap 'custom.cmp' (World Colormap 0) is unavailable; using dflt.cmp's palette instead.",
            Assert.Single(result.Problems));
    }

    [Fact]
    public void ArchiveResolutionUsesWorldColormapZeroInArchivePrecedenceOrder()
    {
        using var first = Gob(("misc/cmp/level.cmp", CmpFile(11)));
        using var second = Gob(("misc/cmp/level.cmp", CmpFile(12)), ("misc/cmp/sector.cmp", CmpFile(20)), ("misc/cmp/dflt.cmp", CmpFile(30)));
        var (result, palette) = MasterColormap.ResolveFromArchives(["level.cmp", "sector.cmp"], [first, second]);
        Assert.Equal("level.cmp", result.Name);
        Assert.Equal(11, palette!.PaletteRgb[3]);
        Assert.Same(palette.PaletteRgb, result.Colormap!.Value.PaletteRgb);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void ArchiveResolutionTreatsAnUnreadableFirstColormapAsUnavailable()
    {
        // The first archive holding the entry decides, as for every other CMP lookup.
        using var damaged = Gob(("misc/cmp/level.cmp", Encoding.ASCII.GetBytes("not a colormap")));
        using var later = Gob(("misc/cmp/level.cmp", CmpFile(12)), ("misc/cmp/dflt.cmp", CmpFile(30)));
        var (result, palette) = MasterColormap.ResolveFromArchives(["level.cmp"], [damaged, later]);
        Assert.Equal("dflt.cmp", result.Name);
        Assert.Equal(30, palette!.PaletteRgb[3]);
        Assert.Contains("'level.cmp' (World Colormap 0) is unavailable", Assert.Single(result.Problems));

        using var empty = Gob(("misc/cmp/other.cmp", CmpFile(40)));
        var (none, nothing) = MasterColormap.ResolveFromArchives(["level.cmp"], [empty]);
        Assert.Null(nothing);
        Assert.Null(none.Colormap);
        Assert.Single(none.Problems);
    }

    private static byte[] CmpFile(byte marker)
    {
        var bytes = new byte[64 + 768 + 16384];
        Encoding.ASCII.GetBytes("CMP ").CopyTo(bytes, 0);
        bytes[64 + 3] = marker;
        return bytes;
    }

    private static GobArchive Gob(params (string Name, byte[] Bytes)[] entries)
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

using Sed.Formats.Gob;

namespace Sed.Formats.Material;

/// <summary>RGBA8 pixels for a resolved material.</summary>
public readonly record struct ResolvedTexture(int Width, int Height, byte[] Rgba);

/// <summary>8-bit palette-indexed pixels for a resolved material.</summary>
public readonly record struct IndexedMaterial(int Width, int Height, byte[] Indices, bool IsSolid = false,
    bool HasColorKey = false);

/// <summary>
/// Resolves JKL material names (e.g. "wall01.mat") to RGBA textures by locating
/// the MAT in one or more resource GOBs and applying a CMP palette. Results are
/// cached; failures cache as null so missing materials are only looked up once.
/// </summary>
public sealed class MaterialLibrary
{
    private readonly GobArchive[] _archives;
    private readonly Colormap _palette;
    private readonly Dictionary<string, ResolvedTexture?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IndexedMaterial?> _indexCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Colormap?> _colormapCache = new(StringComparer.OrdinalIgnoreCase);

    public Colormap Palette => _palette;

    public MaterialLibrary(Colormap palette, params GobArchive[] archives)
    {
        _palette = palette;
        _archives = archives;
    }

    /// <summary>Resolves the named sector CMP in the same archive precedence as materials.</summary>
    public Colormap? GetColormap(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return _palette;
        if (_colormapCache.TryGetValue(name, out var cached)) return cached;
        foreach (var archive in _archives)
        {
            var entry = archive.Find(name);
            if (entry is null) continue;
            try { return _colormapCache[name] = Colormap.Read(archive.ReadBytes(entry)); }
            catch (Exception error) when (error is IOException or ArgumentException)
            { return _colormapCache[name] = null; }
        }
        return _colormapCache[name] = null;
    }

    /// <summary>Loads the level's master palette (first colormap) from the archives, or default.</summary>
    public static Colormap LoadPalette(IEnumerable<string> colorMapNames, params GobArchive[] archives)
    {
        foreach (var name in colorMapNames.Append("dflt.cmp"))
        {
            foreach (var gob in archives)
            {
                var entry = gob.Find(name);
                if (entry is not null)
                    return Colormap.Read(gob.ReadBytes(entry));
            }
        }
        throw new FileNotFoundException("No CMP colormap found (tried level colormaps and dflt.cmp)");
    }

    public ResolvedTexture? Get(string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return null;
        if (_cache.TryGetValue(material, out var cached)) return cached;

        ResolvedTexture? result = null;
        try
        {
            foreach (var gob in _archives)
            {
                var entry = gob.Find(material);
                if (entry is null) continue;
                var mat = MatFile.Read(gob.ReadBytes(entry));
                result = new ResolvedTexture(mat.Width, mat.Height, mat.ToRgba(_palette));
                break;
            }
        }
        catch
        {
            result = null; // unreadable MAT — treat as missing
        }

        _cache[material] = result;
        return result;
    }

    /// <summary>Resolves a material to 8-bit palette indices (for GPU palette+lighting).</summary>
    public IndexedMaterial? GetIndexed(string material)
    {
        if (string.IsNullOrWhiteSpace(material)) return null;
        if (_indexCache.TryGetValue(material, out var cached)) return cached;

        IndexedMaterial? result = null;
        try
        {
            foreach (var gob in _archives)
            {
                var entry = gob.Find(material);
                if (entry is null) continue;
                var mat = MatFile.Read(gob.ReadBytes(entry));
                result = mat.IsColor
                    ? new IndexedMaterial(1, 1, new[] { (byte)(mat.ColorIndex & 0xFF) }, IsSolid: true)
                    : new IndexedMaterial(mat.Width, mat.Height, mat.Indices, HasColorKey: mat.HasColorKey);
                break;
            }
        }
        catch
        {
            result = null;
        }

        _indexCache[material] = result;
        return result;
    }
}

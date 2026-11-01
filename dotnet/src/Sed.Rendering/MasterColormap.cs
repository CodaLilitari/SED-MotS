using Sed.Formats.Gob;
using Sed.Formats.Material;

namespace Sed.Rendering;

/// <summary>The resolved display palette, the CMP it came from, and any fallback taken.</summary>
public sealed record MasterColormapResult(string? Name, ColormapData? Colormap, IReadOnlyList<string> Problems);

/// <summary>
/// JK displays every indexed texture through one palette: the level's first World
/// Colormap. OpenJKDF2's GL renderer uploads <c>sithWorld colormaps[0]</c> as the
/// world palette for all textures (std3D.c); a sector's own CMP supplies its light
/// ramp, and its palette only for solid colours (rdCache). Pass the result to
/// <c>SceneRenderer.SetColormap</c>. The lit snapshot, the editor and the probe
/// tools all choose the palette here, so they cannot disagree.
/// </summary>
public static class MasterColormap
{
    public const string DefaultName = "dflt.cmp";

    /// <summary>
    /// Resolves World Colormap 0, or <see cref="DefaultName"/> when the level lists none.
    /// A listed but unavailable colormap falls back to <see cref="DefaultName"/> with a
    /// stated problem, never to a later sector colormap; when neither resolves, the
    /// colormap is null and that is stated too. Callers report what they do without one.
    /// </summary>
    public static MasterColormapResult Resolve(IReadOnlyList<string> worldColormaps, ColormapLookup lookup)
    {
        ArgumentNullException.ThrowIfNull(worldColormaps);
        ArgumentNullException.ThrowIfNull(lookup);
        string name = worldColormaps.Count > 0 && !string.IsNullOrWhiteSpace(worldColormaps[0]) ? worldColormaps[0] : DefaultName;
        if (lookup(name) is { } master) return new(name, master, []);
        if (name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase))
            return new(null, null, [$"No master palette is available: {DefaultName} is unavailable."]);
        if (lookup(DefaultName) is { } fallback)
            return new(DefaultName, fallback,
                [$"Master colormap '{name}' (World Colormap 0) is unavailable; using {DefaultName}'s palette instead."]);
        return new(null, null,
            [$"No master palette is available: World Colormap 0 '{name}' and {DefaultName} are both unavailable."]);
    }

    /// <summary>
    /// The same rule over resource GOBs in precedence order, for the editor and the probe
    /// tools. Also returns the decoded CMP that their material library needs. A CMP that no
    /// archive holds, or whose first entry is unreadable, is unavailable.
    /// </summary>
    public static (MasterColormapResult Result, Colormap? Palette) ResolveFromArchives(IReadOnlyList<string> worldColormaps,
        IReadOnlyList<GobArchive> archives)
    {
        ArgumentNullException.ThrowIfNull(archives);
        var decoded = new Dictionary<string, Colormap?>(StringComparer.OrdinalIgnoreCase);
        Colormap? Read(string name) =>
            decoded.TryGetValue(name, out var cmp) ? cmp : decoded[name] = MaterialLibrary.FindColormap(name, archives);
        var result = Resolve(worldColormaps, name => Read(name) is { } cmp ? new ColormapData(cmp.PaletteRgb, cmp.LightTable) : null);
        return (result, result.Name is { } found ? Read(found) : null);
    }
}

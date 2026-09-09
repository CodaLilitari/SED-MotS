using Sed.Core.Math;

namespace Sed.Formats.Jkl;

/// <summary>
/// Transient GEORESOURCE tables built while parsing, then referenced by the
/// SECTORS section (mirrors the GVXList/GTXVXList/GSurfList locals in the
/// original loader). Discarded once the model is built.
/// </summary>
internal sealed class GeoData
{
    public List<string> Materials { get; } = new();
    public List<Vec3> Vertices { get; } = new();
    public List<Vec2> TexVertices { get; } = new();
    public List<TempSurface> Surfaces { get; } = new();
    public List<TempAdjoin> Adjoins { get; } = new();

    /// <summary>Global surface index → built model surface (for resolving adjoins).</summary>
    public Dictionary<int, Sed.Core.Model.Surface> SurfaceByGlobalIndex { get; } = new();

    public string GetMaterial(int index) =>
        (uint)index < (uint)Materials.Count ? Materials[index] : string.Empty;
}

/// <summary>A surface as read from GEORESOURCE, before it is bound into a sector.</summary>
internal sealed class TempSurface
{
    public int Material;
    public long SurfFlags;
    public long FaceFlags;
    public int Geo, Light, Tex, Adjoin;
    public ColorF ExtraLight;
    public List<int> Vxs { get; } = new();
    public List<int> Tvxs { get; } = new();
    public List<ColorF> Intensities { get; } = new();
    public List<float?> MonoIntensities { get; } = new();
}

/// <summary>A parsed WORLD ADJOINS record: flags, the mirror (paired) record index,
/// and the global surface index that owns it (set while parsing surfaces).</summary>
internal sealed class TempAdjoin
{
    public long Flags;
    public int Mirror = -1;
    public int Surf = -1;
}

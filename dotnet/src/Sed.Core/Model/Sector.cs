using Sed.Core.Math;

namespace Sed.Core.Model;

/// <summary>A convex room/sector of the level (TJKSector).</summary>
public sealed class Sector
{
    public int Id;
    public int Num;
    public long Flags;

    public ColorF Ambient = ColorF.Black;
    public ColorF ExtraLight = ColorF.Black;
    public ColorF Tint = ColorF.Black;

    public string ColorMap = string.Empty;
    /// <summary>Selected slot in this level's colormap table; -1 means name-only authoring state.</summary>
    public int ColorMapIndex = -1;
    public string Sound = string.Empty;
    public double SoundVolume;
    public Vec3 Thrust = Vec3.Zero;

    public int Layer;

    public Level Level { get; }
    public List<Vertex> Vertices { get; } = new();
    public List<Surface> Surfaces { get; } = new();
    public Box CollideBox;
    public bool HasCollideBox;
    /// <summary>Optional authored engine bounds. Null means compute from current geometry.</summary>
    public Box? BoundBox;
    public Vec3? Center;
    public double? Radius;
    /// <summary>Original vertex positions used to retain authored bounds only while geometry is unchanged.</summary>
    public Vec3[]? BoundsSourceVertices;

    public Sector(Level owner) => Level = owner;

    /// <summary>
    /// Resolves the selected colormap for export/readback. A matching explicit
    /// slot wins even when names repeat. Unbound duplicate names are ambiguous.
    /// The legacy empty-table/unmatched-name fallback is slot 0; with an empty
    /// table this is a scaffold convention, not a verified resource binding.
    /// </summary>
    public int ResolveColorMapIndex() => ResolveColorMapIndex(Level, ColorMap, ColorMapIndex);

    internal static int ResolveColorMapIndex(Level level, string name, int selectedIndex)
    {
        if ((uint)selectedIndex < (uint)level.ColorMaps.Count &&
            level.ColorMaps[selectedIndex].Equals(name, StringComparison.OrdinalIgnoreCase))
            return selectedIndex;
        int found = -1;
        for (int index = 0; index < level.ColorMaps.Count; index++)
        {
            if (!level.ColorMaps[index].Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found >= 0)
                throw new InvalidOperationException($"Colormap name '{name}' is ambiguous without a matching numeric slot. Select an explicit colormap index.");
            found = index;
        }
        return found >= 0 ? found : 0;
    }

    public Vertex AddVertex(Vec3 p)
    {
        var v = new Vertex(p) { Sector = this };
        Vertices.Add(v);
        return v;
    }

    public Surface NewSurface()
    {
        var s = new Surface(this);
        Surfaces.Add(s);
        return s;
    }

    /// <summary>Finds an existing vertex at the given position, or -1 (FindVX).</summary>
    public int FindVertex(Vec3 p, double epsilon = 1e-9)
    {
        for (int i = 0; i < Vertices.Count; i++)
            if (Vertices[i].Position.DistanceSquaredTo(p) <= epsilon)
                return i;
        return -1;
    }

    public void Renumber()
    {
        for (int i = 0; i < Surfaces.Count; i++)
            Surfaces[i].Num = i;
    }
}

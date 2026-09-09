using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Rendering;

/// <summary>
/// Triangulates the level model into a renderable <see cref="Mesh"/>. Each
/// surface is fanned from its first corner; the surface normal is used for
/// shading and a per-surface hue gives flat-shaded faces a distinct look.
/// </summary>
public static class SceneBuilder
{
    /// <summary>Builds a material-batched scene of the level's textured surfaces.</summary>
    public static RenderScene BuildScene(Level level)
    {
        var assembler = new SceneAssembler();
        assembler.AddLevel(level);
        return assembler.Build();
    }

    /// <summary>Builds a mesh of small cubes at each thing's position (for visible markers).</summary>
    public static Mesh BuildThingMarkers(Level level, double size, ColorF color) =>
        BuildThingMarkers(level.Things, size, color);

    /// <summary>Builds marker cubes for a specific set of things.</summary>
    public static Mesh BuildThingMarkers(IEnumerable<Thing> things, double size, ColorF color)
    {
        var mesh = new Mesh();
        foreach (var thing in things)
            AppendCube(mesh, thing.Position, size, color);
        return mesh;
    }

    /// <summary>Builds a single marker cube (e.g. for highlighting a selected thing).</summary>
    public static Mesh BuildMarker(Vec3 center, double size, ColorF color)
    {
        var mesh = new Mesh();
        AppendCube(mesh, center, size, color);
        return mesh;
    }

    /// <summary>
    /// Builds yellow edge beams along the surface perimeter plus orange vertex cubes
    /// at each distinct corner — for selection highlighting without obscuring textures.
    /// </summary>
    public static Mesh BuildEdgeHighlight(Surface surface, double thickness, ColorF edgeColor, ColorF vertexColor)
    {
        var mesh = new Mesh();
        int n = surface.Corners.Count;
        for (int i = 0; i < n; i++)
        {
            var a = surface.Corners[i].Vertex.Position;
            var b = surface.Corners[(i + 1) % n].Vertex.Position;
            AppendBeam(mesh, a, b, thickness, edgeColor);
        }
        var seen = new HashSet<(long, long, long)>();
        foreach (var c in surface.Corners)
        {
            var k = Snap(c.Vertex.Position);
            if (seen.Add(k))
                AppendCube(mesh, c.Vertex.Position, thickness * 1.5, vertexColor);
        }
        return mesh;
    }

    /// <summary>
    /// Builds yellow edge beams for every edge in the sector (deduplicated) plus orange
    /// vertex cubes at every distinct sector vertex.
    /// </summary>
    public static Mesh BuildSectorEdgeHighlight(Sector sector, double thickness, ColorF edgeColor, ColorF vertexColor)
    {
        var mesh = new Mesh();
        var drawn = new HashSet<((long, long, long), (long, long, long))>();
        foreach (var surf in sector.Surfaces)
        {
            int n = surf.Corners.Count;
            for (int i = 0; i < n; i++)
            {
                var a = surf.Corners[i].Vertex.Position;
                var b = surf.Corners[(i + 1) % n].Vertex.Position;
                var ka = Snap(a);
                var kb = Snap(b);
                var key = CompareTuple(ka, kb) <= 0 ? (ka, kb) : (kb, ka);
                if (drawn.Add(key))
                    AppendBeam(mesh, a, b, thickness, edgeColor);
            }
        }
        foreach (var v in sector.Vertices)
            AppendCube(mesh, v.Position, thickness * 1.5, vertexColor);
        return mesh;
    }

    private static (long, long, long) Snap(Vec3 v) =>
        ((long)System.Math.Round(v.X * 10000), (long)System.Math.Round(v.Y * 10000), (long)System.Math.Round(v.Z * 10000));

    private static int CompareTuple((long, long, long) a, (long, long, long) b)
    {
        int c = a.Item1.CompareTo(b.Item1);
        if (c != 0) return c;
        c = a.Item2.CompareTo(b.Item2);
        if (c != 0) return c;
        return a.Item3.CompareTo(b.Item3);
    }

    /// <summary>Builds a thin rectangular prism between two points (an edge beam).</summary>
    private static void AppendBeam(Mesh mesh, Vec3 a, Vec3 b, double thickness, ColorF color)
    {
        var delta = b - a;
        double len = delta.Length;
        if (len < 1e-9) return;
        var dir = delta / len;

        var axis = System.Math.Abs(dir.Y) < 0.9 ? new Vec3(0, 1, 0) : new Vec3(1, 0, 0);
        var right = dir.Cross(axis).Normalized();
        var up = right.Cross(dir).Normalized();

        double t = thickness;
        Vec3[] p =
        {
            a - right*t - up*t, a + right*t - up*t, a + right*t + up*t, a - right*t + up*t,
            b - right*t - up*t, b + right*t - up*t, b + right*t + up*t, b - right*t + up*t,
        };
        int[][] faces =
        {
            new[] { 0,1,2,3 }, new[] { 4,5,6,7 }, new[] { 0,1,5,4 },
            new[] { 3,2,6,7 }, new[] { 0,3,7,4 }, new[] { 1,2,6,5 },
        };
        foreach (var f in faces)
        {
            var n = (p[f[1]] - p[f[0]]).Cross(p[f[2]] - p[f[0]]).Normalized();
            mesh.AddTriangle(
                new MeshVertex(p[f[0]], n, color),
                new MeshVertex(p[f[1]], n, color),
                new MeshVertex(p[f[2]], n, color));
            mesh.AddTriangle(
                new MeshVertex(p[f[0]], n, color),
                new MeshVertex(p[f[2]], n, color),
                new MeshVertex(p[f[3]], n, color));
        }
    }

    private static void AppendCube(Mesh mesh, Vec3 c, double h, ColorF color)
    {
        Vec3[] p =
        {
            c + new Vec3(-h, -h, -h), c + new Vec3(h, -h, -h), c + new Vec3(h, h, -h), c + new Vec3(-h, h, -h),
            c + new Vec3(-h, -h, h), c + new Vec3(h, -h, h), c + new Vec3(h, h, h), c + new Vec3(-h, h, h),
        };
        int[][] faces =
        {
            new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
            new[] { 3, 2, 6, 7 }, new[] { 0, 3, 7, 4 }, new[] { 1, 2, 6, 5 },
        };
        foreach (var f in faces)
        {
            var n = (p[f[1]] - p[f[0]]).Cross(p[f[2]] - p[f[0]]).Normalized();
            mesh.AddTriangle(
                new MeshVertex(p[f[0]], n, color),
                new MeshVertex(p[f[1]], n, color),
                new MeshVertex(p[f[2]], n, color));
            mesh.AddTriangle(
                new MeshVertex(p[f[0]], n, color),
                new MeshVertex(p[f[2]], n, color),
                new MeshVertex(p[f[3]], n, color));
        }
    }

    public static Mesh FromLevel(Level level, Func<Sector, bool>? visibleSector = null)
    {
        var mesh = new Mesh();
        int surfaceCounter = 0;

        foreach (var sector in level.Sectors)
        {
            if (visibleSector is not null && !visibleSector(sector)) continue;
            foreach (var surface in sector.Surfaces)
            {
                if (surface.Corners.Count < 3)
                    continue;

                surface.RecalcNormal();
                var normal = surface.Normal;
                var color = HueColor(surfaceCounter++);

                var c0 = surface.Corners[0].Vertex.Position;
                for (int i = 1; i + 1 < surface.Corners.Count; i++)
                {
                    var ci = surface.Corners[i].Vertex.Position;
                    var cj = surface.Corners[i + 1].Vertex.Position;
                    mesh.AddTriangle(
                        new MeshVertex(c0, normal, color),
                        new MeshVertex(ci, normal, color),
                        new MeshVertex(cj, normal, color));
                }
            }
        }

        return mesh;
    }

    /// <summary>Spreads surfaces across the hue wheel for visual separation.</summary>
    private static ColorF HueColor(int index)
    {
        double h = (index * 0.618033988749895) % 1.0; // golden-ratio hue stepping
        return HsvToRgb(h, 0.55, 0.95);
    }

    private static ColorF HsvToRgb(double h, double s, double v)
    {
        double i = System.Math.Floor(h * 6);
        double f = h * 6 - i;
        double p = v * (1 - s);
        double q = v * (1 - f * s);
        double t = v * (1 - (1 - f) * s);
        return ((int)i % 6) switch
        {
            0 => new ColorF((float)v, (float)t, (float)p),
            1 => new ColorF((float)q, (float)v, (float)p),
            2 => new ColorF((float)p, (float)v, (float)t),
            3 => new ColorF((float)p, (float)q, (float)v),
            4 => new ColorF((float)t, (float)p, (float)v),
            _ => new ColorF((float)v, (float)p, (float)q),
        };
    }
}

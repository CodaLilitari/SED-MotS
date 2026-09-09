using Sed.Core.Math;

namespace Sed.Core.Model;

/// <summary>
/// A polygon surface of a sector (TJKSurface : TPolygon). Holds its vertex
/// references with per-vertex UVs and intensities, material, and adjoin link.
/// </summary>
public sealed class Surface
{
    /// <summary>Per-corner reference into the owning sector's vertex list, plus UV and light.</summary>
    public sealed class Corner
    {
        public Vertex Vertex = null!;
        public TexVertex Uv;
        public ColorF Intensity = ColorF.White;
        /// <summary>Independent MotS monochrome component. Null uses Intensity.R for newly authored scalar lighting.</summary>
        public float? MonoIntensity;
    }

    // Legacy inline flags (prefer SurfaceFlags / FaceFlags constants in EngineFlags.cs).
    public const long SfSkyHorizon = 0x200;
    public const long SfSkyCeiling = 0x400;
    public const long FfTranslucent = 0x02;

    public int Num;
    public long SurfFlags;
    public long FaceFlags;
    public string Material = string.Empty;
    public int MaterialIndex = -1;

    // Engine fields preserved verbatim for faithful save.
    public int Geo = 4;
    public int Light = 3;
    public int Tex = 3;
    public float ExtraLightIntensity;

    /// <summary>True if this surface is a sky (horizon or ceiling) surface.</summary>
    public bool IsSky => (SurfFlags & (SfSkyHorizon | SfSkyCeiling)) != 0;

    /// <summary>True if this surface uses alpha blending.</summary>
    public bool IsTranslucent => (FaceFlags & FfTranslucent) != 0;

    public Surface? Adjoin;
    public long AdjoinFlags;

    public float UScale = 1f;
    public float VScale = 1f;

    /// <summary>
    /// The owning sector. Settable because cleaving a sector moves whole surfaces
    /// from one sector to the other rather than rebuilding them — adjoin partners
    /// point at these objects, so their identity has to survive the split.
    /// </summary>
    public Sector Sector { get; set; }
    public List<Corner> Corners { get; } = new();

    /// <summary>Cached surface normal, recomputed by <see cref="RecalcNormal"/>.</summary>
    public Vec3 Normal { get; private set; }

    public Surface(Sector owner) => Sector = owner;

    /// <summary>
    /// Recomputes the polygon normal using Newell's method, which sums the
    /// contribution of every edge. Taking the cross product of just the first
    /// three corners returns a zero vector whenever those happen to be colinear
    /// — common in real levels — which then corrupts shading, extrude direction
    /// and texture-axis selection. Newell's is immune to that and averages out
    /// numerical noise on near-planar surfaces. Winding convention is unchanged.
    /// A genuinely degenerate (zero-area) surface still yields <see cref="Vec3.Zero"/>.
    /// </summary>
    public void RecalcNormal()
    {
        if (Corners.Count < 3)
        {
            Normal = Vec3.Zero;
            return;
        }

        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < Corners.Count; i++)
        {
            var cur = Corners[i].Vertex.Position;
            var nxt = Corners[(i + 1) % Corners.Count].Vertex.Position;
            nx += (cur.Y - nxt.Y) * (cur.Z + nxt.Z);
            ny += (cur.Z - nxt.Z) * (cur.X + nxt.X);
            nz += (cur.X - nxt.X) * (cur.Y + nxt.Y);
        }

        Normal = new Vec3(nx, ny, nz).Normalized();
    }
}

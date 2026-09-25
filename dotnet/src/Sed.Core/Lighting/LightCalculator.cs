using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Lighting;

/// <summary>Tuning for a lighting bake.</summary>
public sealed class LightingOptions
{
    /// <summary>
    /// Trace a shadow ray from each light to each vertex. Surfaces block light
    /// unless they are an adjoin without <see cref="AdjoinFlags.BlockLight"/> —
    /// i.e. light passes freely through portals by default. Disabling this is
    /// much faster and useful for a quick preview.
    /// </summary>
    public bool CastShadows { get; init; } = true;

    /// <summary>Recompute each lit sector's ambient from its vertex lighting afterwards.</summary>
    public bool UpdateSectorAmbients { get; init; } = true;
}

/// <summary>What a bake did, for reporting back to the user.</summary>
public sealed record LightingStats(int Sectors, int Surfaces, int Vertices, int Lights, int Shadowed);

/// <summary>
/// Static light propagation: point lights → per-vertex intensities, mirroring
/// `CalcLighting` in `LEV_UTILS.PAS`.
///
/// For each surface the light must be in front of the plane; for each corner the
/// light must be within range; the contribution is
/// <c>intensity · ((range − dist) / range)²</c> — quadratic falloff over the
/// remaining range, not inverse-square. Jedi Knight accumulates greyscale,
/// MotS and Infernal Machine accumulate per-channel RGB.
/// MotS may author a separate RGB intensity; its white contribution still uses
/// the primary intensity. Both use the primary range, preserving the historical
/// RGB-range value as metadata without inventing a second falloff rule.
///
/// Results land in <see cref="Surface.Corner.Intensity"/>, so they persist through
/// the existing GEORESOURCE regeneration with no writer changes.
/// </summary>
public static partial class LightCalculator
{
    private const double FacingEpsilon = 0.001;
    private const double EndpointEpsilonSq = 0.0001;

    /// <summary>
    /// Bakes lighting into <paramref name="targets"/> (the whole level when null).
    /// Mutates corner intensities and, unless disabled, sector ambients.
    /// </summary>
    public static LightingStats Calculate(Level level, IReadOnlyList<Sector>? targets = null,
        LightingOptions? options = null)
    {
        options ??= new LightingOptions();

        // Deduplicate: the reset pass is idempotent but the accumulate pass is not,
        // so a sector appearing twice would be lit twice.
        targets = (targets ?? level.Sectors).Distinct().ToList();

        var grid = new SurfaceGrid(BuildPlaneCache(level));
        var lightSectors = new Dictionary<Light, Sector?>();
        foreach (var light in level.Lights)
            lightSectors[light] = FindSector(level, light.Position);

        bool rgb = level.Kind != ProjectType.JediKnight;

        // 1. Reset the vertices we are about to light.
        int vertexCount = 0;
        foreach (var sector in targets)
            foreach (var surf in sector.Surfaces)
                foreach (var c in surf.Corners)
                {
                    c.Intensity = ColorF.Black;
                    c.MonoIntensity = 0;
                    vertexCount++;
                }

        // 2. Accumulate every light's contribution.
        int shadowed = 0;
        foreach (var sector in targets)
        {
            foreach (var surf in sector.Surfaces)
            {
                if (surf.Corners.Count == 0) continue;

                surf.RecalcNormal();
                var origin = surf.Corners[0].Vertex.Position;

                foreach (var light in level.Lights)
                {
                    double rgbIntensity = EffectiveRgbIntensity(level.Kind, light);
                    if (SkipLight(light, rgb, rgbIntensity)) continue;

                    // The light must be on the front side of the surface.
                    if (BehindSurface(surf.Normal, origin, light.Position)) continue;

                    bool sameSector = lightSectors.TryGetValue(light, out var ls) && ReferenceEquals(ls, sector);
                    bool checkShadows = options.CastShadows
                                        && !sameSector
                                        && (light.Flags & LightFlags.NoBlock) == 0;

                    foreach (var corner in surf.Corners)
                    {
                        var sample = EvaluateSample(light, rgbIntensity, corner.Vertex.Position, surf, checkShadows, grid);
                        if (sample.Status == SampleStatus.OutOfRange) continue;
                        if (sample.Status == SampleStatus.Shadowed)
                        {
                            shadowed++;
                            continue;
                        }

                        corner.Intensity = rgb
                            ? corner.Intensity + light.Color * sample.Rgb
                            : Grey(Intensity(corner.Intensity) + sample.Mono);
                        corner.MonoIntensity = (corner.MonoIntensity ?? 0) + sample.Mono;
                    }
                }
            }
        }

        if (options.UpdateSectorAmbients)
            CalculateSectorAmbients(targets);

        int surfaceCount = 0;
        foreach (var s in targets) surfaceCount += s.Surfaces.Count;
        return new LightingStats(targets.Count, surfaceCount, vertexCount, level.Lights.Count, shadowed);
    }

    private static double EffectiveRgbIntensity(ProjectType kind, Light light) =>
        kind == ProjectType.MysteriesOfTheSith ? light.EffectiveRgbIntensity : light.Intensity;

    // Preserve the bake's comparisons, including their existing behavior for invalid data.
    // Diagnostics refuse invalid data separately rather than silently changing the bake.
    private static bool SkipLight(Light light, bool rgb, double rgbIntensity) =>
        light.Range <= 0 || (light.Intensity == 0 && (!rgb || rgbIntensity == 0));

    private static bool BehindSurface(Vec3 normal, Vec3 origin, Vec3 lightPosition) =>
        normal.Dot(lightPosition - origin) < FacingEpsilon;

    private enum SampleStatus { OutOfRange, Shadowed, Eligible }
    private readonly record struct LightSample(SampleStatus Status, float Mono = 0, float Rgb = 0);

    private static LightSample EvaluateSample(Light light, double rgbIntensity, Vec3 target,
        Surface surface, bool checkShadows, SurfaceGrid grid)
    {
        double dist = (light.Position - target).Length;
        if (dist >= light.Range) return new(SampleStatus.OutOfRange);
        if (checkShadows && grid.IsBlocked(light.Position, target, surface)) return new(SampleStatus.Shadowed);
        double falloff = (light.Range - dist) / light.Range;
        return new(SampleStatus.Eligible,
            (float)(light.Intensity * falloff * falloff), (float)(rgbIntensity * falloff * falloff));
    }

    /// <summary>
    /// Sets each sector's ambient to the brighter of its average vertex light and
    /// its average surface extra-light. JK/MotS use nonnegative monochrome samples
    /// without an upper clamp, matching `CalcSectorAmbients` in JED/NJED; RGB and
    /// authored corner values are preserved. Infernal Machine retains colored
    /// ambient calculation. Sectors flagged <see cref="SectorFlags.NoAmbientLight"/>
    /// or without corners are left unchanged.
    /// </summary>
    public static void CalculateSectorAmbients(IReadOnlyList<Sector> sectors)
    {
        foreach (var sector in sectors)
        {
            if ((sector.Flags & SectorFlags.NoAmbientLight) != 0) continue;
            if (sector.Surfaces.Count == 0) continue;

            if (sector.Level.Kind != ProjectType.InfernalMachine)
            {
                double monoLight = 0;
                double extraLight = 0;
                int cornerCount = 0;

                foreach (var surf in sector.Surfaces)
                {
                    extraLight += surf.ExtraLightIntensity;
                    foreach (var corner in surf.Corners)
                    {
                        // Old scalar corners have no independent mono field.
                        // Clamp each sample's lower bound before averaging; bright
                        // samples above 1 remain significant to the authored ambient.
                        monoLight += System.Math.Max(0, corner.MonoIntensity ?? corner.Intensity.R);
                        cornerCount++;
                    }
                }

                if (cornerCount > 0)
                    sector.Ambient = Grey(System.Math.Max(monoLight / cornerCount,
                        extraLight / sector.Surfaces.Count));
                continue;
            }

            var vertexLight = ColorF.Black;
            var surfaceLight = ColorF.Black;
            int n = 0;

            foreach (var surf in sector.Surfaces)
            {
                surfaceLight += Grey(surf.ExtraLightIntensity);
                foreach (var c in surf.Corners)
                {
                    vertexLight += Clamp01(c.Intensity);
                    n++;
                }
            }

            if (n == 0) continue;

            vertexLight *= 1f / n;
            surfaceLight *= 1f / sector.Surfaces.Count;

            if ((sector.Flags & SectorFlags.NoRgbAmbientLight) != 0)
            {
                vertexLight = Grey(Intensity(vertexLight));
                surfaceLight = Grey(Intensity(surfaceLight));
            }

            sector.Ambient = Intensity(vertexLight) > Intensity(surfaceLight) ? vertexLight : surfaceLight;
        }
    }

    // ---- shadow tracing ----

    private readonly record struct SurfacePlane(Surface Surface, Vec3 Normal, double D, Box Bounds);

    /// <summary>
    /// A uniform grid over surface bounding boxes, so a shadow ray only tests the
    /// surfaces near it. Without this, every ray scans the entire level — on a
    /// 10,000-surface level that is billions of box tests for a single bake.
    /// Cells are stored CSR-style (offsets + a flat item array) to avoid
    /// allocating a list per cell.
    /// </summary>
    private sealed class SurfaceGrid
    {
        private readonly SurfacePlane[] _planes;
        private readonly Vec3 _origin;
        private readonly double _invX, _invY, _invZ;
        private readonly int _nx, _ny, _nz;
        private readonly int[] _cellStart;
        private readonly int[] _items;

        // Per-plane visit stamp: a surface spanning several cells is tested once.
        private readonly int[] _stamp;
        private int _query;

        public SurfaceGrid(List<SurfacePlane> planes)
        {
            _planes = planes.ToArray();
            _stamp = new int[_planes.Length];

            var bounds = Box.Empty;
            foreach (var p in _planes)
            {
                bounds.Encapsulate(p.Bounds.Min);
                bounds.Encapsulate(p.Bounds.Max);
            }
            if (_planes.Length == 0) bounds = new Box(Vec3.Zero, Vec3.Zero);

            _origin = bounds.Min;
            var size = bounds.Max - bounds.Min;

            int n = System.Math.Clamp((int)System.Math.Cbrt(System.Math.Max(1, _planes.Length)), 1, 64);
            _nx = size.X > 1e-9 ? n : 1;
            _ny = size.Y > 1e-9 ? n : 1;
            _nz = size.Z > 1e-9 ? n : 1;

            _invX = _nx / System.Math.Max(size.X, 1e-9);
            _invY = _ny / System.Math.Max(size.Y, 1e-9);
            _invZ = _nz / System.Math.Max(size.Z, 1e-9);

            int cellCount = _nx * _ny * _nz;
            var counts = new int[cellCount + 1];

            // Pass 1: how many planes land in each cell.
            for (int i = 0; i < _planes.Length; i++)
                ForEachCell(_planes[i].Bounds, cell => counts[cell + 1]++);

            for (int i = 1; i <= cellCount; i++) counts[i] += counts[i - 1];
            _cellStart = counts;
            _items = new int[_cellStart[cellCount]];

            // Pass 2: fill, using a moving cursor per cell.
            var cursor = new int[cellCount];
            for (int i = 0; i < _planes.Length; i++)
            {
                int index = i;
                ForEachCell(_planes[i].Bounds, cell => _items[_cellStart[cell] + cursor[cell]++] = index);
            }
        }

        private void ForEachCell(Box box, Action<int> visit)
        {
            int x0 = ClampAxis((box.Min.X - _origin.X) * _invX, _nx);
            int x1 = ClampAxis((box.Max.X - _origin.X) * _invX, _nx);
            int y0 = ClampAxis((box.Min.Y - _origin.Y) * _invY, _ny);
            int y1 = ClampAxis((box.Max.Y - _origin.Y) * _invY, _ny);
            int z0 = ClampAxis((box.Min.Z - _origin.Z) * _invZ, _nz);
            int z1 = ClampAxis((box.Max.Z - _origin.Z) * _invZ, _nz);

            for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        visit((z * _ny + y) * _nx + x);
        }

        private static int ClampAxis(double v, int n) =>
            v < 0 ? 0 : v >= n ? n - 1 : (int)v;

        /// <summary>
        /// True when some surface blocks the segment. Adjoins only block when
        /// flagged <see cref="AdjoinFlags.BlockLight"/>, so light passes through
        /// portals; hits at either endpoint are ignored so a surface never
        /// shadows its own vertices.
        /// </summary>
        public bool IsBlocked(Vec3 from, Vec3 to, Surface? exclude)
        {
            var segment = Box.Empty;
            segment.Encapsulate(from);
            segment.Encapsulate(to);

            _query++;
            bool blocked = false;

            ForEachCell(segment, cell =>
            {
                if (blocked) return;
                int start = _cellStart[cell], end = _cellStart[cell + 1];

                for (int i = start; i < end; i++)
                {
                    int index = _items[i];
                    if (_stamp[index] == _query) continue;    // already tested this ray
                    _stamp[index] = _query;

                    ref readonly var plane = ref _planes[index];
                    var surf = plane.Surface;

                    if (ReferenceEquals(surf, exclude)) continue;
                    if (surf.Adjoin is not null && (surf.AdjoinFlags & AdjoinFlags.BlockLight) == 0) continue;
                    if (!Overlaps(plane.Bounds, segment)) continue;

                    var hit = GeometryOps.PlaneSegmentIntersection(from, to, plane.Normal, plane.Normal * plane.D);
                    if (hit is not { } point) continue;

                    if ((point - to).LengthSquared < EndpointEpsilonSq) continue;
                    if ((point - from).LengthSquared < EndpointEpsilonSq) continue;

                    if (PointOnSurface(surf, plane.Normal, point)) { blocked = true; return; }
                }
            });

            return blocked;
        }
    }

    private static List<SurfacePlane> BuildPlaneCache(Level level, bool updateNormals = true)
    {
        var cache = new List<SurfacePlane>();
        foreach (var sector in level.Sectors)
            foreach (var surf in sector.Surfaces)
            {
                if (surf.Corners.Count < 3) continue;
                var normal = GetNormal(surf, updateNormals);

                var box = Box.Empty;
                foreach (var c in surf.Corners) box.Encapsulate(c.Vertex.Position);

                cache.Add(new SurfacePlane(surf, normal, normal.Dot(surf.Corners[0].Vertex.Position), box));
            }
        return cache;
    }

    private static bool Overlaps(Box a, Box b) =>
        a.Min.X <= b.Max.X && a.Max.X >= b.Min.X &&
        a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y &&
        a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;

    /// <summary>Shared with the geometry ops so the two cannot drift apart.</summary>
    private static bool PointOnSurface(Surface surf, Vec3 normal, Vec3 point) =>
        GeometryOps.PointOnSurface(surf, point, normal);

    // ---- sector lookup ----

    /// <summary>
    /// The sector containing a point, or null.
    ///
    /// Uses the baker's historical ray-casting parity and returns the first match.
    /// This is separate from the editor's strict all-candidate spatial selection.
    /// </summary>
    public static Sector? FindSector(Level level, Vec3 point) => FindSector(level, point, updateNormals: true);

    private static Sector? FindSector(Level level, Vec3 point, bool updateNormals)
    {
        // Skewed so the ray is unlikely to graze a shared edge or vertex, which
        // would be counted twice and flip the parity.
        var direction = new Vec3(0.5773502, 0.5771234, 0.5774321).Normalized();

        foreach (var sector in level.Sectors)
        {
            if (sector.Surfaces.Count < 4) continue;      // not a closed volume

            var bounds = Box.Empty;
            foreach (var v in sector.Vertices) bounds.Encapsulate(v.Position);
            if (point.X < bounds.Min.X || point.X > bounds.Max.X ||
                point.Y < bounds.Min.Y || point.Y > bounds.Max.Y ||
                point.Z < bounds.Min.Z || point.Z > bounds.Max.Z)
                continue;

            int crossings = 0;
            foreach (var surf in sector.Surfaces)
            {
                if (surf.Corners.Count < 3) continue;
                var n = GetNormal(surf, updateNormals);
                double denom = n.Dot(direction);
                if (System.Math.Abs(denom) < 1e-12) continue;      // ray parallel to the face

                double d = n.Dot(surf.Corners[0].Vertex.Position);
                double t = (d - n.Dot(point)) / denom;
                if (t <= 1e-9) continue;                            // behind the ray origin

                if (PointOnSurface(surf, n, point + direction * t)) crossings++;
            }

            if ((crossings & 1) == 1) return sector;
        }

        return null;
    }

    private static Vec3 GetNormal(Surface surface, bool updateCache)
    {
        if (!updateCache) return surface.CalculateNormal();
        surface.RecalcNormal();
        return surface.Normal;
    }

    // ---- colour helpers (TColorF semantics) ----

    /// <summary>Average of the channels — `TColorF.Intensity`.</summary>
    private static float Intensity(ColorF c) => (c.R + c.G + c.B) / 3f;

    private static ColorF Grey(double v) => new((float)v, (float)v, (float)v);

    /// <summary>`TColorF.Normalize` — clamps each channel to 0..1.</summary>
    private static ColorF Clamp01(ColorF c) => new(
        System.Math.Clamp(c.R, 0f, 1f),
        System.Math.Clamp(c.G, 0f, 1f),
        System.Math.Clamp(c.B, 0f, 1f));
}

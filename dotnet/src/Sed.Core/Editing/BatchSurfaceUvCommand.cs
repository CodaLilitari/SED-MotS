using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>The directed normal of a planar UV projection, not a viewing direction.</summary>
public enum SurfaceUvProjectionDirection { PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ }

/// <summary>
/// A prepared, atomic UV edit for an exact surface set. Preparation validates and
/// computes every corner before any write. Apply and undo preflight the entire
/// saved geometry and UV state, so a stale final surface cannot partially edit
/// earlier surfaces. Only corner UV fields are ever written.
/// </summary>
public sealed class BatchSurfaceUvCommand : IEditCommand
{
    public const int MaxSurfaces = 4096;
    public const int MaxCornersPerSurface = 64;
    public const int MaxTotalCorners = 65536;
    public const double MaxUvMagnitude = 1_000_000;

    private sealed record Entry(Surface Surface, Sector Sector, Surface.Corner[] Corners,
        Vertex[] Vertices, Vec3[] Positions, TexVertex[] Before, TexVertex[] After);

    private readonly Entry[] _entries;
    private bool _applied;
    public string Name { get; }

    private BatchSurfaceUvCommand(string name, Entry[] entries) { Name = name; _entries = entries; }

    /// <summary>
    /// Map all selected positions relative to one world origin at a fixed positive
    /// texels-per-world-unit scale. Bases (U,V) are +X:(+Y,+Z), -X:(-Y,+Z),
    /// +Y:(-X,+Z), -Y:(+X,+Z), +Z:(+X,+Y), -Z:(-X,+Y).
    /// U cross V is always the directed normal; V is not flipped for screen rows.
    /// Equal world positions get identical UVs, including across sector boundaries.
    /// Edge-on projections and projected numerical collapse are refused.
    /// </summary>
    public static BatchSurfaceUvCommand Project(IReadOnlyList<Surface> surfaces,
        SurfaceUvProjectionDirection direction, Vec3 origin, double texelsPerUnit)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentException("Unknown UV projection direction.", nameof(direction));
        if (!Finite(origin)) throw new ArgumentException("Projection origin must be finite.", nameof(origin));
        if (!double.IsFinite(texelsPerUnit) || texelsPerUnit <= 0)
            throw new ArgumentException("Texels per unit must be finite and positive.", nameof(texelsPerUnit));

        var entries = Prepare(surfaces, (position, _) =>
        {
            var relative = position - origin;
            var uv = ProjectPair(relative, direction);
            return new TexVertex(uv.U * texelsPerUnit, uv.V * texelsPerUnit);
        });
        foreach (var entry in entries)
        {
            var normal = UnitNormal(entry.Positions);
            double facing = direction switch
            {
                SurfaceUvProjectionDirection.PositiveX or SurfaceUvProjectionDirection.NegativeX => normal.X,
                SurfaceUvProjectionDirection.PositiveY or SurfaceUvProjectionDirection.NegativeY => normal.Y,
                _ => normal.Z
            };
            if (System.Math.Abs(facing) <= 1e-10)
                throw new ArgumentException("A selected surface is edge-on to the projection.", nameof(surfaces));
            ValidatePolygon2D(entry.After, "The projected UV polygon collapses or intersects at this origin and scale.");
        }
        return new BatchSurfaceUvCommand("Project selected surface UVs", entries);
    }

    /// <summary>
    /// Apply to every corner: pivot + rotate((old - pivot) * scale) + offset.
    /// Rotation is in degrees, counterclockwise in mathematical U-right/V-up
    /// coordinates. The same explicit UV pivot serves every selected surface.
    /// Negative scales mirror; zero scales are refused. Existing seams coincide
    /// when their input UVs coincide; this operation does not weld unrelated UVs.
    /// </summary>
    public static BatchSurfaceUvCommand Transform(IReadOnlyList<Surface> surfaces,
        TexVertex pivot, TexVertex scale, double rotationDegrees, TexVertex offset)
    {
        if (!Finite(pivot) || !Finite(scale) || !Finite(offset) || !double.IsFinite(rotationDegrees))
            throw new ArgumentException("UV pivot, scale, rotation and offset must be finite.");
        if (scale.U == 0 || scale.V == 0)
            throw new ArgumentException("UV scales must be nonzero.", nameof(scale));
        double angle = (rotationDegrees % 360) * (System.Math.PI / 180);
        double cos = System.Math.Cos(angle), sin = System.Math.Sin(angle);
        var entries = Prepare(surfaces, (_, old) =>
        {
            double u = (old.U - pivot.U) * scale.U;
            double v = (old.V - pivot.V) * scale.V;
            return new TexVertex(pivot.U + u * cos - v * sin + offset.U,
                pivot.V + u * sin + v * cos + offset.V);
        });
        return new BatchSurfaceUvCommand("Transform selected surface UVs", entries);
    }

    public void Apply() => Write(true);
    public void Revert() => Write(false);

    private void Write(bool apply)
    {
        // No mutation until every member passes. UV equality is bitwise so undo
        // restores exact original doubles, including the sign of zero.
        foreach (var entry in _entries)
        {
            var surface = entry.Surface;
            if (!ReferenceEquals(surface.Sector, entry.Sector) || !Attached(surface)
                || surface.Corners.Count != entry.Corners.Length)
                throw new InvalidOperationException("Surface topology changed since the batch UV edit was prepared.");
            var expected = _applied ? entry.After : entry.Before;
            for (int i = 0; i < entry.Corners.Length; i++)
            {
                var corner = surface.Corners[i];
                if (!ReferenceEquals(corner, entry.Corners[i]) || !ReferenceEquals(corner.Vertex, entry.Vertices[i])
                    || !ReferenceEquals(corner.Vertex.Sector, entry.Sector) || !entry.Sector.Vertices.Contains(corner.Vertex)
                    || corner.Vertex.Position != entry.Positions[i] || !SameUv(corner.Uv, expected[i]))
                    throw new InvalidOperationException("Surface geometry or UVs changed since the batch UV edit was prepared.");
            }
        }
        foreach (var entry in _entries)
        {
            var values = apply ? entry.After : entry.Before;
            for (int i = 0; i < values.Length; i++) entry.Corners[i].Uv = values[i];
        }
        _applied = apply;
    }

    private static Entry[] Prepare(IReadOnlyList<Surface> surfaces, Func<Vec3, TexVertex, TexVertex> calculate)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        if (surfaces.Count is < 1 or > MaxSurfaces)
            throw new ArgumentException($"Select between one and {MaxSurfaces} surfaces.", nameof(surfaces));
        var unique = new HashSet<Surface>();
        var uniqueCorners = new HashSet<Surface.Corner>();
        Level? level = null;
        int cornerCount = 0;
        var entries = new List<Entry>(surfaces.Count);
        foreach (var surface in surfaces)
        {
            if (surface is null || !unique.Add(surface))
                throw new ArgumentException("Selected surfaces must be nonnull and unique.", nameof(surfaces));
            if (!Attached(surface))
                throw new ArgumentException("Every surface must belong to a sector in the level.", nameof(surfaces));
            level ??= surface.Sector.Level;
            if (!ReferenceEquals(level, surface.Sector.Level))
                throw new ArgumentException("Selected surfaces must belong to one level.", nameof(surfaces));
            if (surface.Corners.Count is < 3 or > MaxCornersPerSurface)
                throw new ArgumentException($"Surfaces need three to {MaxCornersPerSurface} corners.", nameof(surfaces));
            cornerCount += surface.Corners.Count;
            if (cornerCount > MaxTotalCorners)
                throw new ArgumentException($"Selection exceeds {MaxTotalCorners} corners.", nameof(surfaces));

            var corners = surface.Corners.ToArray();
            var vertices = new Vertex[corners.Length];
            var positions = new Vec3[corners.Length];
            var before = new TexVertex[corners.Length];
            var after = new TexVertex[corners.Length];
            var uniqueVertices = new HashSet<Vertex>();
            for (int i = 0; i < corners.Length; i++)
            {
                var c = corners[i];
                if (c is null || c.Vertex is null || !uniqueCorners.Add(c) || !uniqueVertices.Add(c.Vertex)
                    || !ReferenceEquals(c.Vertex.Sector, surface.Sector) || !surface.Sector.Vertices.Contains(c.Vertex))
                    throw new ArgumentException("Corners need distinct vertex references belonging to their sector; corner objects cannot be shared.", nameof(surfaces));
                if (!Finite(c.Vertex.Position) || !Finite(c.Uv))
                    throw new ArgumentException("Selected vertices and UVs must be finite.", nameof(surfaces));
                vertices[i] = c.Vertex;
                positions[i] = c.Vertex.Position;
                before[i] = c.Uv;
                after[i] = calculate(c.Vertex.Position, c.Uv);
                if (!Finite(after[i]) || System.Math.Abs(after[i].U) > MaxUvMagnitude || System.Math.Abs(after[i].V) > MaxUvMagnitude)
                    throw new ArgumentException($"The batch UV operation must produce finite coordinates within +/-{MaxUvMagnitude}.", nameof(surfaces));
            }
            ValidateGeometry(positions);
            entries.Add(new Entry(surface, surface.Sector, corners, vertices, positions, before, after));
        }
        return entries.ToArray();
    }

    private static void ValidateGeometry(Vec3[] positions)
    {
        var normalized = NormalizePositions(positions);
        var normal = UnitNormalNormalized(normalized);
        if (normalized.Any(p => System.Math.Abs(normal.Dot(p)) > 1e-8))
            throw new ArgumentException("UV editing requires planar surfaces.");
        double ax = System.Math.Abs(normal.X), ay = System.Math.Abs(normal.Y), az = System.Math.Abs(normal.Z);
        var direction = ax >= ay && ax >= az ? SurfaceUvProjectionDirection.PositiveX
            : ay >= az ? SurfaceUvProjectionDirection.PositiveY : SurfaceUvProjectionDirection.PositiveZ;
        ValidatePolygon2D(normalized.Select(p => ProjectPair(p, direction)).ToArray(),
            "UV editing requires simple, nondegenerate surface polygons.");
    }

    private static Vec3[] NormalizePositions(Vec3[] positions)
    {
        var local = positions.Select(p => p - positions[0]).ToArray();
        double extent = local.Max(p => System.Math.Max(System.Math.Abs(p.X), System.Math.Max(System.Math.Abs(p.Y), System.Math.Abs(p.Z))));
        if (!double.IsFinite(extent) || extent <= 0)
            throw new ArgumentException("Surface coordinates have a degenerate or unrepresentable extent.");
        return local.Select(p => p / extent).ToArray();
    }

    private static Vec3 UnitNormal(Vec3[] positions) => UnitNormalNormalized(NormalizePositions(positions));
    private static Vec3 UnitNormalNormalized(Vec3[] points)
    {
        var normal = Vec3.Zero;
        for (int i = 0; i < points.Length; i++) normal += points[i].Cross(points[(i + 1) % points.Length]);
        if (!Finite(normal) || normal.Length <= 1e-12)
            throw new ArgumentException("Surface polygon has no usable area.");
        return normal.Normalized();
    }

    private static void ValidatePolygon2D(TexVertex[] points, string message)
    {
        var local = points.Select(p => new TexVertex(p.U - points[0].U, p.V - points[0].V)).ToArray();
        double extent = local.Max(p => System.Math.Max(System.Math.Abs(p.U), System.Math.Abs(p.V)));
        if (!double.IsFinite(extent) || extent <= 0) throw new ArgumentException(message);
        local = local.Select(p => new TexVertex(p.U / extent, p.V / extent)).ToArray();
        double area = 0;
        for (int i = 0; i < local.Length; i++)
        {
            var a = local[i]; var b = local[(i + 1) % local.Length];
            if (System.Math.Abs(a.U - b.U) + System.Math.Abs(a.V - b.V) <= 1e-12)
                throw new ArgumentException(message);
            area += a.U * b.V - a.V * b.U;
            for (int j = i + 2; j < local.Length; j++)
            {
                if (i == 0 && j == local.Length - 1) continue;
                if (Intersects(a, b, local[j], local[(j + 1) % local.Length])) throw new ArgumentException(message);
            }
        }
        if (!double.IsFinite(area) || System.Math.Abs(area) <= 1e-12) throw new ArgumentException(message);
    }

    private static bool Intersects(TexVertex a, TexVertex b, TexVertex c, TexVertex d)
    {
        double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
        if (((abC > 0 && abD < 0) || (abC < 0 && abD > 0))
            && ((cdA > 0 && cdB < 0) || (cdA < 0 && cdB > 0))) return true;
        return OnSegment(a, b, c, abC) || OnSegment(a, b, d, abD)
            || OnSegment(c, d, a, cdA) || OnSegment(c, d, b, cdB);
    }

    private static double Cross(TexVertex a, TexVertex b, TexVertex p)
        => (b.U - a.U) * (p.V - a.V) - (b.V - a.V) * (p.U - a.U);
    private static bool OnSegment(TexVertex a, TexVertex b, TexVertex p, double cross)
        => System.Math.Abs(cross) <= 1e-12
            && p.U >= System.Math.Min(a.U, b.U) - 1e-12 && p.U <= System.Math.Max(a.U, b.U) + 1e-12
            && p.V >= System.Math.Min(a.V, b.V) - 1e-12 && p.V <= System.Math.Max(a.V, b.V) + 1e-12;

    private static TexVertex ProjectPair(Vec3 p, SurfaceUvProjectionDirection direction) => direction switch
    {
        SurfaceUvProjectionDirection.PositiveX => new(p.Y, p.Z),
        SurfaceUvProjectionDirection.NegativeX => new(-p.Y, p.Z),
        SurfaceUvProjectionDirection.PositiveY => new(-p.X, p.Z),
        SurfaceUvProjectionDirection.NegativeY => new(p.X, p.Z),
        SurfaceUvProjectionDirection.PositiveZ => new(p.X, p.Y),
        SurfaceUvProjectionDirection.NegativeZ => new(-p.X, p.Y),
        _ => throw new ArgumentException("Unknown UV projection direction.")
    };
    private static bool Attached(Surface surface) => surface.Sector is not null
        && surface.Sector.Level.Sectors.Contains(surface.Sector) && surface.Sector.Surfaces.Contains(surface);
    private static bool Finite(Vec3 p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
    private static bool Finite(TexVertex p) => double.IsFinite(p.U) && double.IsFinite(p.V);
    private static bool SameUv(TexVertex a, TexVertex b) => BitConverter.DoubleToInt64Bits(a.U) == BitConverter.DoubleToInt64Bits(b.U)
        && BitConverter.DoubleToInt64Bits(a.V) == BitConverter.DoubleToInt64Bits(b.V);
}

using System.Security.Cryptography;
using System.Text;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Query;

public sealed record SpatialSectorMatch(Sector Sector, int SectorIndex, Box GeometryBounds,
    Vec3 BoundsCenter, Vec3 VertexCentroid, bool OnBoundary);

public sealed record SpatialSurfaceMatch(Surface Surface, int SectorIndex, int SurfaceIndex,
    Box GeometryBounds, Vec3 BoundsCenter, Vec3 Centroid, Vec3 InwardNormal,
    Vec3 OutwardNormal, bool? PointOnBoundary);

/// <summary>
/// Pure spatial queries for closed, inward-wound convex sectors. Bounds are a
/// broad phase only. Queries return all matches, including boundaries; callers
/// requiring one target must reject both missing and ambiguous matches.
/// </summary>
public static class SpatialSelection
{
    public const double DefaultTolerance = 1e-7;
    public const double DefaultAngleToleranceDegrees = 0.1;

    public static IReadOnlyList<SpatialSectorMatch> SelectSectors(Level level, Vec3 point,
        double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(level);
        ValidateTolerance(tolerance);
        RequireFinite(point, nameof(point));
        ValidateLevelOwners(level);
        var matches = new List<SpatialSectorMatch>();
        for (int sectorIndex = 0; sectorIndex < level.Sectors.Count; sectorIndex++)
        {
            var sector = level.Sectors[sectorIndex];
            // Inspect ownership and all referenced coordinates before culling:
            // a dangling corner must not hide outside a guessed vertex AABB.
            var context = DescribeStructure(sector, sectorIndex);
            if (!InBounds(context.Bounds, point, tolerance)) continue;
            var normals = ValidateConvex(context, tolerance);
            bool inside = true, boundary = false;
            for (int i = 0; i < sector.Surfaces.Count; i++)
            {
                double distance = SignedDistance(normals[i], point, sector.Surfaces[i].Corners[0].Vertex.Position,
                    context, i);
                if (distance < -tolerance) inside = false;
                if (System.Math.Abs(distance) <= tolerance) boundary = true;
            }
            if (inside) matches.Add(new(sector, context.Index, context.Bounds,
                BoundsCenter(context.Bounds), context.Centroid, boundary));
        }
        return matches;
    }

    public static IReadOnlyList<SpatialSurfaceMatch> SelectSurfaces(Sector sector, Vec3 outwardDirection,
        double angleToleranceDegrees = DefaultAngleToleranceDegrees, Vec3? surfacePoint = null,
        double tolerance = DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(sector);
        ValidateTolerance(tolerance);
        if (!double.IsFinite(angleToleranceDegrees) || angleToleranceDegrees is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(angleToleranceDegrees), "Angular tolerance must be finite and between 0 and 5 degrees.");
        RequireFinite(outwardDirection, nameof(outwardDirection));
        var direction = NormalizeDirection(outwardDirection);
        if (surfacePoint is { } point) RequireFinite(point, nameof(surfacePoint));
        var context = DescribeStructure(sector);
        var normals = ValidateConvex(context, tolerance);
        var matches = new List<SpatialSurfaceMatch>();
        double minimumDot = System.Math.Cos(angleToleranceDegrees * System.Math.PI / 180);
        for (int i = 0; i < sector.Surfaces.Count; i++)
        {
            var surface = sector.Surfaces[i];
            var inward = normals[i]; var outward = inward * -1;
            // Allow only roundoff at the angular threshold, not a geometric
            // distance tolerance (which has different units).
            if (outward.Dot(direction) + 1e-14 < minimumDot) continue;
            bool? boundary = null;
            if (surfacePoint is { } candidate)
            {
                if (System.Math.Abs(SignedDistance(inward, candidate, surface.Corners[0].Vertex.Position,
                    context, i)) > tolerance) continue;
                bool contained = true, onEdge = false;
                for (int corner = 0; corner < surface.Corners.Count; corner++)
                {
                    var a = surface.Corners[corner].Vertex.Position;
                    var b = surface.Corners[(corner + 1) % surface.Corners.Count].Vertex.Position;
                    double distance = EdgeDistance(a, b, candidate, inward, context, i);
                    if (distance < -tolerance) contained = false;
                    if (System.Math.Abs(distance) <= tolerance) onEdge = true;
                }
                if (!contained) continue;
                boundary = onEdge;
            }
            var bounds = Box.Empty;
            var centroid = Vec3.Zero;
            foreach (var corner in surface.Corners)
            {
                bounds.Encapsulate(corner.Vertex.Position);
                centroid += corner.Vertex.Position / surface.Corners.Count;
            }
            if (!Finite(centroid)) throw Unsupported(context, i, "corner centroid is not finite");
            matches.Add(new(surface, context.Index, i, bounds, BoundsCenter(bounds), centroid,
                inward, outward, boundary));
        }
        return matches;
    }

    /// <summary>
    /// Hashes the selected address and full ordered enclosing-sector geometry.
    /// Mutable labels, cached normals, materials, flags, UVs and lights are not
    /// geometry. Positions retain their exact IEEE754 bits, including signed zero.
    /// </summary>
    public static string SectorGeometryDigest(Sector sector) => GeometryDigest(sector, null);

    /// <summary>Includes the whole enclosing sector, so moving another boundary invalidates this surface binding.</summary>
    public static string SurfaceGeometryDigest(Surface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return GeometryDigest(surface.Sector, surface);
    }

    private static string GeometryDigest(Sector sector, Surface? selectedSurface)
    {
        ArgumentNullException.ThrowIfNull(sector);
        var context = DescribeStructure(sector);
        int surfaceIndex = selectedSurface is null ? -1 : sector.Surfaces.IndexOf(selectedSurface);
        if (selectedSurface is not null && surfaceIndex < 0)
            throw Unsupported(context, null, "selected surface is not a live member of its owner");
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SED-SPATIAL-GEOMETRY-1");
            writer.Write(selectedSurface is null ? "sector" : "surface");
            writer.Write((int)sector.Level.Kind);
            writer.Write(context.Index); writer.Write(surfaceIndex);
            writer.Write(sector.Vertices.Count);
            foreach (var vertex in sector.Vertices)
            {
                writer.Write(vertex.Position.X); writer.Write(vertex.Position.Y); writer.Write(vertex.Position.Z);
            }
            writer.Write(sector.Surfaces.Count);
            foreach (var surface in sector.Surfaces)
            {
                writer.Write(surface.Corners.Count);
                foreach (var corner in surface.Corners) writer.Write(context.VertexIndices[corner.Vertex]);
                var endpoint = AdjoinAddress(context, surface);
                writer.Write(endpoint.Sector); writer.Write(endpoint.Surface);
            }
        }
        return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)))).ToLowerInvariant();
    }

    private sealed record Context(Sector Sector, int Index, Dictionary<Vertex, int> VertexIndices,
        Box Bounds, Vec3 Centroid);

    private static Context DescribeStructure(Sector sector, int? validatedIndex = null)
    {
        // The level-wide query already checked ownership and knows this index.
        // Standalone face queries and digests still validate their entry point.
        if (validatedIndex is null) ValidateLevelOwners(sector.Level);
        int index = validatedIndex ?? sector.Level.Sectors.IndexOf(sector);
        if (index < 0) throw new InvalidOperationException("Spatial selection requires a live sector in its owning level.");
        var vertices = new Dictionary<Vertex, int>();
        var context = new Context(sector, index, vertices, Box.Empty, Vec3.Zero);
        var bounds = Box.Empty;
        var centroid = Vec3.Zero;
        if (sector.Vertices.Count == 0) throw Unsupported(context, null, "geometry bounds are unknown because the sector has no vertices");
        for (int i = 0; i < sector.Vertices.Count; i++)
        {
            var vertex = sector.Vertices[i];
            if (vertex is null || vertex.Sector != sector || !vertices.TryAdd(vertex, i))
                throw Unsupported(context, null, "vertices must be distinct live objects owned by the sector");
            if (!Finite(vertex.Position)) throw Unsupported(context, null, $"vertex {i} has nonfinite coordinates; geometry bounds are unknown");
            bounds.Encapsulate(vertex.Position);
            centroid += vertex.Position / sector.Vertices.Count;
        }
        if (!Finite(centroid)) throw Unsupported(context, null, "vertex centroid is not finite");
        context = context with { Bounds = bounds, Centroid = centroid };
        var surfaces = new HashSet<Surface>();
        for (int i = 0; i < sector.Surfaces.Count; i++)
        {
            var surface = sector.Surfaces[i];
            if (surface is null || surface.Sector != sector || !surfaces.Add(surface))
                throw Unsupported(context, i, "surfaces must be distinct live objects owned by the sector");
            foreach (var corner in surface.Corners)
                if (corner is null || corner.Vertex is null || !vertices.ContainsKey(corner.Vertex))
                    throw Unsupported(context, i, "a corner does not reference a vertex in its owning sector");
            AdjoinAddress(context, surface);
        }
        return context;
    }

    private static Vec3[] ValidateConvex(Context context, double tolerance)
    {
        var sector = context.Sector;
        if (sector.Vertices.Count < 4 || sector.Surfaces.Count < 4)
            throw Unsupported(context, null, "a closed convex volume needs at least four vertices and four faces");
        var normals = new Vec3[sector.Surfaces.Count];
        var edges = new Dictionary<(int A, int B), int>();
        var usedVertices = new HashSet<int>();
        for (int i = 0; i < sector.Surfaces.Count; i++)
        {
            var face = sector.Surfaces[i];
            if (face.Corners.Count is < 3 or > 24)
                throw Unsupported(context, i, "a supported convex polygon needs 3 to 24 corners");
            if (face.Corners.Select(c => c.Vertex).Distinct().Count() != face.Corners.Count)
                throw Unsupported(context, i, "the polygon repeats a vertex");
            var normal = face.CalculateNormal();
            if (!Finite(normal) || System.Math.Abs(normal.LengthSquared - 1) > 1e-10)
                throw Unsupported(context, i, "the polygon has no finite unit normal");
            normals[i] = normal;
            var origin = face.Corners[0].Vertex.Position;
            if (SignedDistance(normal, context.Centroid, origin, context, i) <= tolerance)
                throw Unsupported(context, i, "faces must point inward around a volume thicker than the selected tolerance");
            foreach (var corner in face.Corners)
                if (System.Math.Abs(SignedDistance(normal, corner.Vertex.Position, origin, context, i)) > tolerance)
                    throw Unsupported(context, i, "the polygon is not planar within the selected tolerance");
            foreach (var vertex in sector.Vertices)
                if (SignedDistance(normal, vertex.Position, origin, context, i) < -tolerance)
                    throw Unsupported(context, i, "the sector is nonconvex or a face points outward");
            for (int j = 0; j < face.Corners.Count; j++)
            {
                var a = face.Corners[j].Vertex;
                var b = face.Corners[(j + 1) % face.Corners.Count].Vertex;
                foreach (var corner in face.Corners)
                    if (EdgeDistance(a.Position, b.Position, corner.Vertex.Position, normal, context, i) < -tolerance)
                        throw Unsupported(context, i, "the polygon boundary is not convex");
                int ai = context.VertexIndices[a], bi = context.VertexIndices[b];
                usedVertices.Add(ai);
                if (!edges.TryAdd((ai, bi), i))
                    throw Unsupported(context, i, "a directed boundary edge is repeated");
            }
        }
        if (usedVertices.Count != sector.Vertices.Count)
            throw Unsupported(context, null, "the sector contains vertices unused by its boundary");
        var adjacent = Enumerable.Range(0, sector.Surfaces.Count).Select(_ => new List<int>()).ToArray();
        foreach (var edge in edges)
        {
            if (!edges.TryGetValue((edge.Key.B, edge.Key.A), out int other) || other == edge.Value)
                throw Unsupported(context, edge.Value, "the boundary is not closed: each edge must appear once in each direction");
            adjacent[edge.Value].Add(other);
        }
        var visited = new HashSet<int> { 0 };
        var queue = new Queue<int>(); queue.Enqueue(0);
        while (queue.TryDequeue(out int face))
            foreach (int neighbor in adjacent[face]) if (visited.Add(neighbor)) queue.Enqueue(neighbor);
        if (visited.Count != sector.Surfaces.Count)
            throw Unsupported(context, null, "the boundary has disconnected shells");
        return normals;
    }

    private static (int Sector, int Surface) AdjoinAddress(Context context, Surface surface)
    {
        if (surface.Adjoin is not { } adjoin) return (-1, -1);
        var target = adjoin.Sector;
        int sectorIndex = target is null ? -1 : context.Sector.Level.Sectors.IndexOf(target);
        int surfaceIndex = target is null ? -1 : target.Surfaces.IndexOf(adjoin);
        if (adjoin == surface || target == context.Sector || sectorIndex < 0 || surfaceIndex < 0
            || target!.Level != context.Sector.Level || target.Surfaces.Count(s => s == adjoin) != 1 || adjoin.Adjoin != surface)
            throw Unsupported(context, context.Sector.Surfaces.IndexOf(surface), "the adjoin does not reference a distinct live reciprocal endpoint in this level");
        return (sectorIndex, surfaceIndex);
    }

    private static void ValidateLevelOwners(Level level)
    {
        var sectors = new HashSet<Sector>();
        foreach (var sector in level.Sectors)
            if (sector is null || sector.Level != level || !sectors.Add(sector))
                throw new InvalidOperationException("Spatial selection requires distinct sectors belonging to their owning level.");
    }

    private static double SignedDistance(Vec3 normal, Vec3 point, Vec3 origin, Context context, int face)
    {
        double distance = normal.Dot(point - origin);
        if (!double.IsFinite(distance)) throw Unsupported(context, face, "plane distance cannot be represented finitely");
        return distance;
    }

    private static double EdgeDistance(Vec3 a, Vec3 b, Vec3 point, Vec3 normal, Context context, int face)
    {
        var edge = b - a;
        double length = edge.Length;
        if (!double.IsFinite(length) || length <= 1e-9)
            throw Unsupported(context, face, "a polygon edge is nonfinite or no longer than 1e-9 world units");
        double distance = (edge / length).Cross(point - a).Dot(normal);
        if (!double.IsFinite(distance)) throw Unsupported(context, face, "polygon edge distance cannot be represented finitely");
        return distance;
    }

    private static Vec3 NormalizeDirection(Vec3 direction)
    {
        double scale = System.Math.Max(System.Math.Abs(direction.X), System.Math.Max(System.Math.Abs(direction.Y), System.Math.Abs(direction.Z)));
        if (scale == 0) throw new ArgumentException("Outward direction must be nonzero.", nameof(direction));
        return (direction / scale).Normalized();
    }

    private static bool InBounds(Box bounds, Vec3 point, double tolerance) =>
        point.X >= bounds.Min.X - tolerance && point.X <= bounds.Max.X + tolerance &&
        point.Y >= bounds.Min.Y - tolerance && point.Y <= bounds.Max.Y + tolerance &&
        point.Z >= bounds.Min.Z - tolerance && point.Z <= bounds.Max.Z + tolerance;

    private static Vec3 BoundsCenter(Box bounds) => bounds.Min * 0.5 + bounds.Max * 0.5;
    private static bool Finite(Vec3 point) => double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
    private static void RequireFinite(Vec3 point, string name)
    {
        if (!Finite(point)) throw new ArgumentException("Coordinates must be finite.", name);
    }
    private static void ValidateTolerance(double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance is < 1e-9 or > 1e-3)
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Distance tolerance must be finite and between 1e-9 and 1e-3 world units.");
    }
    private static InvalidOperationException Unsupported(Context context, int? face, string detail) =>
        new($"Spatial selection does not support sector {context.Index}{(face is int index ? $", surface {index}" : "")}: {detail}.");
}

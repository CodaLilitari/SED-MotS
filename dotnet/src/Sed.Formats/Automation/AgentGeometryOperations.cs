using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    internal static bool TryPrepareGeometryOperation(JklDocument doc, JsonElement op, out IEditCommand? command)
    {
        command = null;
        string name = String(op, "op");
        if (name is not ("extrudeSurface" or "adjoinSurfaces")) return false;
        if (doc.Level.Kind is not (ProjectType.JediKnight or ProjectType.MysteriesOfTheSith))
            throw new AgentRequestException("Geometry authoring supports JK and MotS only.");
        EnsureKnownGeometrySections(doc);
        Fields(op, name == "extrudeSurface" ? ["op", "sector", "surface", "distance"]
            : ["op", "sector", "surface", "otherSector", "otherSurface"]);
        var sector = At(doc.Level.Sectors, Integer(op, "sector"), "sector");
        var surface = At(sector.Surfaces, Integer(op, "surface"), "surface");
        ValidateAuthoringSector(sector);
        if (surface.Adjoin is not null) throw FieldError("The source surface is already adjoined.", "surface");
        if (name == "extrudeSurface")
        {
            double distance = Number(op, "distance", 0.0001, 100000);
            if (ExtrudeSurfaceCommand.Validate(surface, distance) is { } problem) throw new AgentRequestException(problem);
            EnsureExtrusionDoesNotOverlap(doc.Level, surface, distance);
            command = new ExtrudeSurfaceCommand(surface, distance);
        }
        else
        {
            var otherSector = At(doc.Level.Sectors, Integer(op, "otherSector"), "otherSector");
            var other = At(otherSector.Surfaces, Integer(op, "otherSurface"), "otherSurface");
            if (sector == otherSector) throw FieldError("An adjoin must join two different sectors.", "otherSector");
            ValidateAuthoringSector(otherSector);
            if (other.Adjoin is not null) throw FieldError("The other surface is already adjoined.", "otherSurface");
            if (!MatchingOppositeBoundaries(surface, other))
                throw FieldError("Adjoin surfaces must have the same complete boundary in reverse order and opposite normals; partial overlaps are not supported.", "otherSurface");
            command = new CompositeCommand("Adjoin open surfaces", [new MakeAdjoinCommand(surface, other),
                new SetSurfaceGeoCommand(surface, 0), new SetSurfaceGeoCommand(other, 0)]);
        }
        return true;
    }

    internal static object GeometryOperationEffect(JklDocument doc, JsonElement op, int? created)
    {
        int index = Integer(op, "sector");
        var sector = At(doc.Level.Sectors, index, "sector");
        if (String(op, "op") == "extrudeSurface")
            return new { target = new { kind = "extrusion", sector = index, surface = Integer(op, "surface"), createdSector = created },
                state = new { source = GeometrySectorState(doc.Level, sector),
                    appended = created is int appended ? GeometrySectorState(doc.Level, At(doc.Level.Sectors, appended, "sector")) : null } };
        int otherIndex = Integer(op, "otherSector");
        var otherSector = At(doc.Level.Sectors, otherIndex, "otherSector");
        return new { target = new { kind = "adjoin", sector = index, surface = Integer(op, "surface"),
                otherSector = otherIndex, otherSurface = Integer(op, "otherSurface") },
            state = new { source = GeometrySurfaceState(doc.Level, At(sector.Surfaces, Integer(op, "surface"), "surface")),
                other = GeometrySurfaceState(doc.Level, At(otherSector.Surfaces, Integer(op, "otherSurface"), "otherSurface")) } };
    }

    private static object GeometrySectorState(Level level, Sector sector) => new
    {
        sector = level.Sectors.IndexOf(sector), sector.Flags, ambient = sector.Ambient.R, extraLight = sector.ExtraLight.R,
        tint = new[] { sector.Tint.R, sector.Tint.G, sector.Tint.B }, sector.ColorMap, sector.Sound, sector.SoundVolume,
        thrust = Position(sector.Thrust), sector.Layer, sector.HasCollideBox,
        vertices = sector.Vertices.Select(v => Position(v.Position)).ToArray(),
        surfaces = sector.Surfaces.Select(s => GeometrySurfaceState(level, s)).ToArray(),
    };

    private static object GeometrySurfaceState(Level level, Surface surface) => new
    {
        sector = level.Sectors.IndexOf(surface.Sector), surface = surface.Sector.Surfaces.IndexOf(surface),
        surface.Material, surface.MaterialIndex, surface.SurfFlags, surface.FaceFlags, surface.Geo, surface.Light, surface.Tex,
        surface.ExtraLightIntensity, surface.AdjoinFlags,
        adjoinSector = surface.Adjoin is null ? -1 : level.Sectors.IndexOf(surface.Adjoin.Sector),
        adjoinSurface = surface.Adjoin is null ? -1 : surface.Adjoin.Sector.Surfaces.IndexOf(surface.Adjoin),
        corners = surface.Corners.Select(c => new { vertex = surface.Sector.Vertices.IndexOf(c.Vertex),
            position = Position(c.Vertex.Position), uv = new[] { c.Uv.U, c.Uv.V }, mono = c.MonoIntensity ?? c.Intensity.R,
            rgb = new[] { c.Intensity.R, c.Intensity.G, c.Intensity.B } }).ToArray(),
    };

    private static void ValidateAuthoringSector(Sector sector)
    {
        const double epsilon = 1e-7;
        if (!sector.Level.Sectors.Contains(sector) || sector.Surfaces.Count < 4 || sector.Vertices.Count < 4)
            throw new AgentRequestException("Geometry authoring requires a live closed convex sector.");
        var edges = new Dictionary<(Vertex, Vertex), int>();
        var center = TransformVerticesCommand.Centroid(sector.Vertices);
        foreach (var surface in sector.Surfaces)
        {
            if (surface.Corners.Count is < 3 or > 24 || surface.Corners.Any(c => c.Vertex is null || !sector.Vertices.Contains(c.Vertex)
                || !double.IsFinite(c.Vertex.Position.X) || !double.IsFinite(c.Vertex.Position.Y) || !double.IsFinite(c.Vertex.Position.Z)))
                throw new AgentRequestException("Geometry authoring requires valid polygons with 3 to 24 corners.");
            surface.RecalcNormal();
            var normal = surface.Normal;
            var origin = surface.Corners[0].Vertex.Position;
            if (normal.LengthSquared < .99 || normal.Dot(center - origin) <= epsilon)
                throw new AgentRequestException("Geometry authoring requires every sector face to point inward around a nonzero volume.");
            if (surface.Corners.Any(c => System.Math.Abs(normal.Dot(c.Vertex.Position - origin)) > epsilon)
                || sector.Vertices.Any(v => normal.Dot(v.Position - origin) < -epsilon))
                throw new AgentRequestException("Geometry authoring requires planar faces and a convex sector.");
            for (int i = 0; i < surface.Corners.Count; i++)
            {
                var a = surface.Corners[i].Vertex; var b = surface.Corners[(i + 1) % surface.Corners.Count].Vertex;
                if ((a.Position - b.Position).LengthSquared <= 1e-18)
                    throw new AgentRequestException("Geometry authoring refuses zero-length polygon edges.");
                if (surface.Corners.Any(c => (b.Position - a.Position).Cross(c.Vertex.Position - a.Position).Dot(normal) < -epsilon))
                    throw new AgentRequestException("Geometry authoring requires convex polygon boundaries.");
                edges[(a, b)] = edges.GetValueOrDefault((a, b)) + 1;
            }
        }
        if (edges.Any(e => e.Value != 1 || edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) != 1))
            throw new AgentRequestException("Geometry authoring requires a closed manifold sector with each boundary edge used twice in opposite directions.");
    }

    private static bool MatchingOppositeBoundaries(Surface a, Surface b)
    {
        const double toleranceSquared = 1e-12;
        int count = a.Corners.Count;
        if (count != b.Corners.Count || a.Normal.Dot(b.Normal) > -1 + 1e-8) return false;
        for (int start = 0; start < count; start++)
        {
            bool match = true;
            for (int i = 0; i < count; i++)
                if (a.Corners[i].Vertex.Position.DistanceSquaredTo(b.Corners[(start - i + count) % count].Vertex.Position) > toleranceSquared)
                { match = false; break; }
            if (match) return true;
        }
        return false;
    }

    private static void EnsureExtrusionDoesNotOverlap(Level level, Surface surface, double distance)
    {
        const double epsilon = 1e-8;
        var normal = surface.Normal;
        var front = surface.Corners.Select(c => c.Vertex.Position).ToArray();
        var back = front.Select(p => p - normal * distance).ToArray();
        var points = front.Concat(back).ToArray();
        if (points.Any(p => System.Math.Abs(p.X) > 1_000_000 || System.Math.Abs(p.Y) > 1_000_000 || System.Math.Abs(p.Z) > 1_000_000))
            throw FieldError("Extrusion exceeds the authoring coordinate range of +/-1000000.", "distance");
        var planes = new List<(Vec3 origin, Vec3 normal)> { (front[0], normal * -1), (back[0], normal) };
        for (int i = 0; i < front.Length; i++)
            planes.Add((front[i], (front[(i + 1) % front.Length] - front[i]).Cross(normal * -1).Normalized()));
        foreach (var sector in level.Sectors)
        {
            if (sector == surface.Sector) continue;
            ValidateAuthoringSector(sector);
            bool separated = sector.Surfaces.Any(face => points.All(p => face.Normal.Dot(p - face.Corners[0].Vertex.Position) <= epsilon))
                || planes.Any(plane => sector.Vertices.All(v => plane.normal.Dot(v.Position - plane.origin) <= epsilon));
            if (!separated)
                throw FieldError($"Extrusion may overlap existing sector {level.Sectors.IndexOf(sector)}. Use a free boundary or adjoin exactly matching existing faces.", "distance");
        }
    }

    private static void EnsureKnownGeometrySections(JklDocument doc)
    {
        try
        {
            JklGeometrySectionPolicy.EnsureSupported(doc);
        }
        catch (InvalidOperationException error)
        {
            throw new AgentRequestException(error.Message, innerException: error);
        }
    }
}

using System.Text;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Query;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    /// <summary>Discover exact spatial matches without editing or guessing a target.</summary>
    public static object Select(string path, string requestText) => AgentSelections.Run(path, requestText);
}

/// <summary>Source-bound spatial discovery and fail-closed operation addressing.</summary>
public static class AgentSelections
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    internal static string? TargetKind(string operation) => operation switch
    {
        "setSectorFlags" or "setSectorAmbient" or "setSectorColormap" => "sector",
        "setSurfaceMaterial" or "setSurfaceFlags" or "fitSurfaceTexture" or "setSurfaceUv" or "alignSurfaceTexture"
            or "setSurfaceRenderModes" or "extrudeSurface" or "adjoinSurfaces" => "surface",
        _ => null,
    };

    public static object Schema()
    {
        object Variant(string kind)
        {
            var properties = new Dictionary<string, object>
            {
                ["schemaVersion"] = new { @const = 1 }, ["kind"] = new { @const = kind },
                ["sectorPoint"] = VectorSchema(),
                ["tolerance"] = new { type = "number", minimum = 1e-9, maximum = 1e-3, @default = 1e-7 },
                ["limit"] = new { type = "integer", minimum = 1, maximum = MaxLimit, @default = DefaultLimit },
            };
            if (kind == "surface")
            {
                properties["outward"] = VectorSchema();
                properties["angularToleranceDegrees"] = new { type = "number", minimum = 0, maximum = 5, @default = .1 };
                properties["surfacePoint"] = VectorSchema();
            }
            return new { type = "object", additionalProperties = false,
                required = kind == "sector" ? new[] { "schemaVersion", "kind", "sectorPoint" }
                    : new[] { "schemaVersion", "kind", "sectorPoint", "outward" }, properties };
        }
        return new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "SED MotS spatial selection version 1",
            ["description"] = "Read-only convex-sector containment and outward face selection. Boundaries are inclusive. All matches determine uniqueness before limit truncates candidates. A surface requires exactly one containing sector. Only a unique result carries a source- and geometry-bound token; metadata edits may reuse it, geometry or portal changes require selecting again. outward must be finite and nonzero; surfacePoint must lie on the actual polygon.",
            ["oneOf"] = new[] { Variant("sector"), Variant("surface") },
        };
    }

    internal static object TokenSchema(string kind)
    {
        var properties = new Dictionary<string, object>
        {
            ["schemaVersion"] = new { @const = 1 }, ["kind"] = new { @const = kind },
            ["sourceRevision"] = HashSchema(), ["geometrySha256"] = HashSchema(),
            ["sector"] = new { type = "integer", minimum = 0, maximum = int.MaxValue },
        };
        if (kind == "surface") properties["surface"] = new { type = "integer", minimum = 0, maximum = int.MaxValue };
        return new { type = "object", additionalProperties = false, required = properties.Keys.ToArray(), properties };
    }

    private static object VectorSchema() => new { type = "array", minItems = 3, maxItems = 3,
        items = new { type = "number", minimum = -1_000_000, maximum = 1_000_000 } };
    private static object HashSchema() => new { type = "string", pattern = "^[0-9a-fA-F]{64}$" };

    public static object Run(string path, string requestText)
    {
        if (Encoding.UTF8.GetByteCount(requestText) > AgentWorkspace.MaxRequestBytes)
            throw new AgentRequestException("Selection exceeds the 1 MiB request limit.");
        using var json = JsonDocument.Parse(requestText, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        CheckObject(root, "");
        string kind = Text(root, "kind", "");
        if (kind is not ("sector" or "surface")) throw Request("kind must be sector or surface.", "/kind");
        CheckFields(root, kind == "sector" ? ["schemaVersion", "kind", "sectorPoint", "tolerance", "limit"]
            : ["schemaVersion", "kind", "sectorPoint", "tolerance", "limit", "outward", "angularToleranceDegrees", "surfacePoint"], "");
        _ = Integer(root, "schemaVersion", null, 1, 1, "");
        Vec3 point = Vector(root, "sectorPoint");
        double tolerance = Number(root, "tolerance", 1e-7, 1e-9, 1e-3);
        int limit = Integer(root, "limit", DefaultLimit, 1, MaxLimit, "");
        Vec3? outward = kind == "surface" ? Vector(root, "outward") : null;
        if (outward is Vec3 direction && direction.X == 0 && direction.Y == 0 && direction.Z == 0)
            throw Request("outward must be a nonzero vector.", "/outward");
        double angle = kind == "surface" ? Number(root, "angularToleranceDegrees", .1, 0, 5) : .1;
        Vec3? surfacePoint = root.TryGetProperty("surfacePoint", out _) ? Vector(root, "surfacePoint") : null;
        var snapshot = AgentWorkspace.LoadWithProvenance(path);
        try
        {
            var sectors = SpatialSelection.SelectSectors(snapshot.Document.Level, point, tolerance);
            if (kind == "sector" || sectors.Count != 1)
            {
                object? token = kind == "sector" && sectors.Count == 1 ? SectorToken(snapshot, sectors[0].Sector) : null;
                return Reply("sector", sectors.Count, sectors.Take(limit).Select(SectorCandidate).ToArray(), token);
            }
            var surfaces = SpatialSelection.SelectSurfaces(sectors[0].Sector, outward!.Value, angle, surfacePoint, tolerance);
            return Reply("surface", surfaces.Count, surfaces.Take(limit).Select(SurfaceCandidate).ToArray(),
                surfaces.Count == 1 ? SurfaceToken(snapshot, surfaces[0].Surface) : null);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new AgentRequestException(error.Message, innerException: error);
        }

        object Reply(string stage, int total, object[] candidates, object? selection) => new
        {
            schemaVersion = 1, kind, stage, status = total == 0 ? "none" : total == 1 ? "unique" : "ambiguous",
            inputSha256 = snapshot.InputSha256, sourceRevision = snapshot.SourceRevision, dependencies = snapshot.Dependencies,
            total, limit, returnedCount = candidates.Length, candidates, selection,
        };
    }

    private static object SectorCandidate(SpatialSectorMatch match) => new
    {
        kind = "sector", sector = match.SectorIndex, geometryBounds = Bounds(match.GeometryBounds),
        boundsCenter = Position(match.BoundsCenter), vertexCentroid = Position(match.VertexCentroid), match.OnBoundary,
    };
    private static object SurfaceCandidate(SpatialSurfaceMatch match) => new
    {
        kind = "surface", sector = match.SectorIndex, surface = match.SurfaceIndex,
        geometryBounds = Bounds(match.GeometryBounds), boundsCenter = Position(match.BoundsCenter), centroid = Position(match.Centroid),
        inwardNormal = Position(match.InwardNormal), outwardNormal = Position(match.OutwardNormal), match.PointOnBoundary,
    };
    private static object SectorToken(AgentDocumentSnapshot snapshot, Sector sector) => new
    {
        schemaVersion = 1, kind = "sector", sourceRevision = snapshot.SourceRevision,
        sector = snapshot.Document.Level.Sectors.IndexOf(sector), geometrySha256 = SpatialSelection.SectorGeometryDigest(sector),
    };
    private static object SurfaceToken(AgentDocumentSnapshot snapshot, Surface surface) => new
    {
        schemaVersion = 1, kind = "surface", sourceRevision = snapshot.SourceRevision,
        sector = snapshot.Document.Level.Sectors.IndexOf(surface.Sector), surface = surface.Sector.Surfaces.IndexOf(surface),
        geometrySha256 = SpatialSelection.SurfaceGeometryDigest(surface),
    };
    private static double[] Position(Vec3 value) => [value.X, value.Y, value.Z];
    private static object Bounds(Box value) => new { min = Position(value.Min), max = Position(value.Max) };

    internal static (JsonElement Operation, object? Selections) BindOperation(AgentDocumentSnapshot snapshot, JsonElement operation)
    {
        CheckObject(operation, "");
        bool hasTarget = operation.TryGetProperty("target", out var target);
        bool hasOther = operation.TryGetProperty("otherTarget", out var other);
        if (!hasTarget && !hasOther) return (operation.Clone(), null);
        string name = Text(operation, "op", "");
        string? kind = TargetKind(name);
        if (kind is null) throw Request($"{name} does not support selection tokens.", hasTarget ? "/target" : "/otherTarget");
        if (!hasTarget) throw Request("target is required when using otherTarget.", "/target");
        bool paired = name is "adjoinSurfaces" or "alignSurfaceTexture";
        if (hasOther != paired)
            throw Request(paired ? $"{name} requires target and otherTarget together."
                : "otherTarget is only supported by paired-surface operations.", "/otherTarget");
        foreach (string field in new[] { "sector", "surface", "otherSector", "otherSurface" })
            if (operation.TryGetProperty(field, out _))
                throw Request("Selection tokens cannot be mixed with integer sector/surface addressing in the same operation.", "/" + field);
        var bound = BindToken(snapshot, target, kind, "/target");
        var second = hasOther ? BindToken(snapshot, other, "surface", "/otherTarget") : ((int Sector, int? Surface)?)null;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in operation.EnumerateObject())
                if (property.Name is not ("target" or "otherTarget")) property.WriteTo(writer);
            writer.WriteNumber("sector", bound.Sector);
            if (bound.Surface is int face) writer.WriteNumber("surface", face);
            if (second is { } otherBound)
            {
                writer.WriteNumber("otherSector", otherBound.Sector);
                writer.WriteNumber("otherSurface", otherBound.Surface!.Value);
            }
            writer.WriteEndObject();
        }
        using var normalized = JsonDocument.Parse(buffer.ToArray());
        return (normalized.RootElement.Clone(), new { target = target.Clone(), otherTarget = hasOther ? (JsonElement?)other.Clone() : null });
    }

    private static (int Sector, int? Surface) BindToken(AgentDocumentSnapshot snapshot, JsonElement token, string kind, string pointer)
    {
        CheckObject(token, pointer);
        CheckFields(token, kind == "sector" ? ["schemaVersion", "kind", "sourceRevision", "sector", "geometrySha256"]
            : ["schemaVersion", "kind", "sourceRevision", "sector", "surface", "geometrySha256"], pointer);
        _ = Integer(token, "schemaVersion", null, 1, 1, pointer);
        if (Text(token, "kind", pointer) != kind) throw Request($"This operation requires a {kind} selection token.", pointer + "/kind");
        string revision = ReadHash(token, "sourceRevision", pointer);
        string digest = ReadHash(token, "geometrySha256", pointer);
        int sectorIndex = Integer(token, "sector", null, 0, int.MaxValue, pointer);
        int? surfaceIndex = kind == "surface" ? Integer(token, "surface", null, 0, int.MaxValue, pointer) : null;
        if (!revision.Equals(snapshot.SourceRevision, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException("Selection sourceRevision is stale. Select again from the current source.", jsonPointer: pointer + "/sourceRevision");
        var level = snapshot.Document.Level;
        if ((uint)sectorIndex >= (uint)level.Sectors.Count) throw Request("Selection sector is outside the current document.", pointer + "/sector");
        var sector = level.Sectors[sectorIndex];
        if (surfaceIndex is int index && (uint)index >= (uint)sector.Surfaces.Count)
            throw Request("Selection surface is outside the selected sector.", pointer + "/surface");
        string actual = surfaceIndex is int selected ? SpatialSelection.SurfaceGeometryDigest(sector.Surfaces[selected])
            : SpatialSelection.SectorGeometryDigest(sector);
        if (!digest.Equals(actual, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException("Selection geometry changed. Apply/reopen and select again; this token cannot retarget within a batch.", jsonPointer: pointer + "/geometrySha256");
        return (sectorIndex, surfaceIndex);
    }

    private static AgentRequestException Request(string message, string pointer) => new(message, jsonPointer: pointer);
    private static void CheckObject(JsonElement root, string pointer)
    {
        if (root.ValueKind != JsonValueKind.Object) throw Request("Expected a JSON object.", pointer);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name)) throw Request($"Duplicate property '{property.Name}'.", pointer + "/" + property.Name);
    }
    private static void CheckFields(JsonElement root, string[] allowed, string pointer)
    {
        foreach (var property in root.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Request($"Unsupported property '{property.Name}'.", pointer + "/" + property.Name);
    }
    private static string Text(JsonElement root, string name, string pointer)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String)
            throw Request($"{name} must be a string.", pointer + "/" + name);
        return field.GetString()!;
    }
    private static string ReadHash(JsonElement root, string name, string pointer)
    {
        string text = Text(root, name, pointer);
        if (text.Length != 64 || text.Any(c => !Uri.IsHexDigit(c)))
            throw Request($"{name} must be a 64-digit SHA256.", pointer + "/" + name);
        return text;
    }
    private static int Integer(JsonElement root, string name, int? fallback, int minimum, int maximum, string pointer)
    {
        if (!root.TryGetProperty(name, out var field))
            return fallback ?? throw Request($"Missing required property '{name}'.", pointer + "/" + name);
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out int value) || value < minimum || value > maximum)
            throw Request($"{name} must be an integer between {minimum} and {maximum}.", pointer + "/" + name);
        return value;
    }
    private static double Number(JsonElement root, string name, double fallback, double min, double max)
    {
        if (!root.TryGetProperty(name, out var field)) return fallback;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetDouble(out double value) || !double.IsFinite(value) || value < min || value > max)
            throw Request($"{name} must be finite and between {min} and {max}.", "/" + name);
        return value;
    }
    private static Vec3 Vector(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.Array || field.GetArrayLength() != 3)
            throw Request($"{name} must have exactly three finite coordinates.", "/" + name);
        double[] values = new double[3]; int index = 0;
        foreach (var item in field.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double value) || !double.IsFinite(value) || System.Math.Abs(value) > 1_000_000)
                throw Request($"{name} coordinates must be finite and within +/-1000000.", $"/{name}/{index}");
            values[index++] = value;
        }
        return new(values[0], values[1], values[2]);
    }
}

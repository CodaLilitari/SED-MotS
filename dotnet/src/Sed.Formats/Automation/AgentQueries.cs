using System.Text;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Query;
using Sed.Formats.Jkl;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    /// <summary>Read-only, paginated inspection of editable data with source provenance.</summary>
    public static object Query(string path, string requestText) => AgentQueries.Run(path, requestText);
}

/// <summary>Bounded readback and discovery without changing the legacy inspect response.</summary>
public static class AgentQueries
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxResponseBytes = 1024 * 1024;
    private static readonly string[] Collections =
        ["summary", "sectors", "things", "thingContainment", "lights", "cogs", "templates", "materials", "colormaps", "surfaces", "vertices", "architecture", "architecturalVertices"];
    private static readonly string[] NamedCollections = ["things", "thingContainment", "templates", "materials", "colormaps"];

    public static object Schema() => new Dictionary<string, object>
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["title"] = "SED MotS agent query version 1",
        ["description"] = "Read-only pages. Indices are from the unfiltered collection and bound to sourceRevision. Surface and vertex indices are local to the selected sector. Search is case-insensitive; things match name or template, other named collections match name. Optional exact filters are collection-specific and AND with search; offset counts filtered matches. Responses retain full fields, may shorten a page to fit 1 MiB, and expose nextOffset; one oversized item is refused.",
        ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new[] { "schemaVersion", "collection" },
        ["properties"] = new Dictionary<string, object>
        {
            ["schemaVersion"] = new Dictionary<string, object> { ["const"] = 1 },
            ["collection"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = Collections },
            ["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue, @default = 0 },
            ["limit"] = new { type = "integer", minimum = 1, maximum = MaxLimit, @default = DefaultLimit },
            ["sector"] = new { type = "integer", minimum = 0, maximum = int.MaxValue },
            ["lightingIndex"] = new { type = "integer", minimum = 0, maximum = int.MaxValue },
            ["mesh"] = new { type = "integer", minimum = 0, maximum = int.MaxValue },
            ["search"] = new { type = "string", minLength = 1, maxLength = 127, pattern = @"\S" },
            ["filter"] = new { type = "object", minProperties = 1 },
        },
        ["allOf"] = new object[]
        {
            new Dictionary<string, object>
            {
                ["if"] = new { properties = new { collection = new Dictionary<string, object> { ["const"] = "architecturalVertices" } } },
                ["then"] = new { required = new[] { "lightingIndex", "mesh" } },
                ["else"] = new { properties = new { lightingIndex = false, mesh = false } },
            },
            new Dictionary<string, object>
            {
                ["if"] = new { properties = new { collection = new Dictionary<string, object> { ["enum"] = new[] { "surfaces", "vertices" } } } },
                ["then"] = new { required = new[] { "sector" } },
                ["else"] = new { properties = new { sector = false } },
            },
            new Dictionary<string, object>
            {
                ["if"] = new { properties = new { collection = new Dictionary<string, object> { ["enum"] = NamedCollections } } },
                ["else"] = new { properties = new { search = false } },
            },
            new Dictionary<string, object>
            {
                ["if"] = new { properties = new { collection = new Dictionary<string, object> { ["enum"] = AgentQueryFilter.Collections } } },
                ["else"] = new { properties = new { filter = false } },
            },
        }.Concat(AgentQueryFilter.Collections.Select(collection => (object)new Dictionary<string, object>
        {
            ["if"] = new { properties = new { collection = new Dictionary<string, object> { ["const"] = collection } } },
            ["then"] = new { properties = new Dictionary<string, object> { ["filter"] = AgentQueryFilter.Schema(collection) } },
        })).ToArray(),
    };

    private sealed record Request(string Collection, int Offset, int Limit, int? Sector, string? Search, int? LightingIndex, int? Mesh, AgentQueryFilter? Filter);
    private sealed record Catalog(int Count, Func<int, object> Item, Func<int, bool>? Matches = null);

    internal static object Run(string path, string requestText)
    {
        var request = ParseRequest(requestText);
        var snapshot = AgentWorkspace.LoadWithProvenance(path);
        var catalog = BuildCatalog(snapshot.Document, request);
        int[] indices = Enumerable.Range(0, catalog.Count).Where(i => catalog.Matches?.Invoke(i) ?? true).ToArray();
        object[] items = indices.Skip(request.Offset).Take(request.Limit).Select(catalog.Item).ToArray();

        object Page(int count) => new
        {
            schemaVersion = 1, inputSha256 = snapshot.InputSha256,
            sourceRevision = snapshot.SourceRevision, dependencies = snapshot.Dependencies,
            sourceLightMetadata = AgentWorkspace.DescribeSourceLightMetadata(snapshot.Document),
            collection = request.Collection, sector = request.Sector, search = request.Search,
            filter = request.Filter?.Json,
            lightingIndex = request.LightingIndex, mesh = request.Mesh,
            total = indices.Length, sourceTotal = catalog.Count, offset = request.Offset,
            limit = request.Limit, returnedCount = count,
            nextOffset = (long)request.Offset + count < indices.Length ? (int?)(request.Offset + count) : null,
            items = count == items.Length ? items : items.Take(count).ToArray(),
        };

        var page = Page(items.Length);
        if (Size(page) <= MaxResponseBytes) return page;
        if (items.Length == 0 || Size(Page(1)) > MaxResponseBytes)
            throw new AgentRequestException("The selected query item or its provenance exceeds the 1 MiB response limit. Inspect that source record directly; fields were not truncated.");

        // Keep complete records while shrinking an oversized page. Binary search
        // avoids repeatedly serializing the entire requested page one item at a time.
        int low = 1, high = items.Length - 1;
        while (low < high)
        {
            int candidate = low + (high - low + 1) / 2;
            if (Size(Page(candidate)) <= MaxResponseBytes) low = candidate;
            else high = candidate - 1;
        }
        return Page(low);
    }

    private static int Size(object response) => JsonSerializer.SerializeToUtf8Bytes(response, AgentWorkspace.JsonOptions).Length;

    private static Request ParseRequest(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > AgentWorkspace.MaxRequestBytes)
            throw new AgentRequestException("Query exceeds the 1 MiB request limit.");
        using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AgentRequestException("Query must be a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        string[] allowed = ["schemaVersion", "collection", "offset", "limit", "sector", "search", "lightingIndex", "mesh", "filter"];
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new AgentRequestException($"Duplicate or unsupported query property '{property.Name}'.");
        int version = Integer(root, "schemaVersion", null, 1, 1);
        _ = version;
        if (!root.TryGetProperty("collection", out var collectionField) || collectionField.ValueKind != JsonValueKind.String)
            throw new AgentRequestException("collection must name a query collection; use schema query.");
        string collection = collectionField.GetString()!;
        if (!Collections.Contains(collection, StringComparer.Ordinal))
            throw new AgentRequestException($"Unsupported query collection '{collection}'. Use schema query.");
        int offset = Integer(root, "offset", 0, 0, int.MaxValue);
        int limit = Integer(root, "limit", DefaultLimit, 1, MaxLimit);
        int? sector = root.TryGetProperty("sector", out _) ? Integer(root, "sector", null, 0, int.MaxValue) : null;
        bool needsSector = collection is "surfaces" or "vertices";
        if (needsSector && sector is null) throw new AgentRequestException("sector is required for surfaces and vertices queries.");
        if (!needsSector && sector is not null) throw new AgentRequestException("sector is only valid for surfaces and vertices queries.");
        string? search = null;
        if (root.TryGetProperty("search", out var field))
        {
            if (!NamedCollections.Contains(collection, StringComparer.Ordinal))
                throw new AgentRequestException("search is only valid for things, thingContainment, templates, materials and colormaps queries.");
            if (field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()) || field.GetString()!.Length > 127)
                throw new AgentRequestException("search must contain between 1 and 127 characters and cannot be blank.");
            search = field.GetString();
        }
        bool samples = collection == "architecturalVertices";
        int? lightingIndex = root.TryGetProperty("lightingIndex", out _) ? Integer(root, "lightingIndex", null, 0, int.MaxValue) : null;
        int? mesh = root.TryGetProperty("mesh", out _) ? Integer(root, "mesh", null, 0, int.MaxValue) : null;
        if (samples && (lightingIndex is null || mesh is null))
            throw new AgentRequestException("architecturalVertices requires lightingIndex and mesh from query architecture.");
        if (!samples && (lightingIndex is not null || mesh is not null))
            throw new AgentRequestException("lightingIndex and mesh are only valid for architecturalVertices.");
        var filter = root.TryGetProperty("filter", out var filterField) ? AgentQueryFilter.Parse(filterField, collection) : null;
        return new Request(collection, offset, limit, sector, search, lightingIndex, mesh, filter);
    }

    private static int Integer(JsonElement root, string name, int? fallback, int minimum, int maximum)
    {
        if (!root.TryGetProperty(name, out var value))
            return fallback ?? throw new AgentRequestException($"Missing required query property '{name}'.");
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result) || result < minimum || result > maximum)
            throw new AgentRequestException($"{name} must be an integer between {minimum} and {maximum}.");
        return result;
    }

    private static Catalog BuildCatalog(JklDocument document, Request request)
    {
        var level = document.Level;
        if (request.Filter?.MaterialIndex is int materialIndex && materialIndex >= document.Materials.Count)
            throw new AgentRequestException($"filter.materialIndex {materialIndex} is outside the current material table ({document.Materials.Count} materials).");
        bool Match(string text) => request.Search is null || text.Contains(request.Search, StringComparison.OrdinalIgnoreCase);
        Sector SelectedSector()
        {
            int index = request.Sector!.Value;
            if ((uint)index >= (uint)level.Sectors.Count)
                throw new AgentRequestException($"sector index {index} is outside the current document ({level.Sectors.Count} sectors).");
            return level.Sectors[index];
        }
        switch (request.Collection)
        {
            case "summary":
                return new Catalog(1, _ => Summary(document));
            case "sectors":
                return new Catalog(level.Sectors.Count, i =>
                {
                    var sector = level.Sectors[i];
                    return new
                    {
                        index = i, sector.Num, sector.Flags, sector.Layer,
                        ambient = sector.Ambient.R, extraLight = sector.ExtraLight.R, tint = Rgb(sector.Tint),
                        sector.ColorMap, sector.ColorMapIndex, sector.Sound, sector.SoundVolume, thrust = Position(sector.Thrust),
                        vertices = sector.Vertices.Count, surfaces = sector.Surfaces.Count,
                        geometryBounds = GeometryBounds(sector.Vertices.Select(v => v.Position)),
                        neighbors = sector.Surfaces.Where(s => s.Adjoin is not null).Select(s => level.Sectors.IndexOf(s.Adjoin!.Sector)).Distinct().Order().ToArray(),
                        boundBox = Bounds(sector.BoundBox), center = sector.Center is Vec3 center ? Position(center) : null,
                        sector.Radius, sector.HasCollideBox, collideBox = sector.HasCollideBox ? Bounds(sector.CollideBox) : null,
                    };
                }, i => request.Filter?.Matches(level.Sectors[i]) ?? true);
            case "things":
                return new Catalog(level.Things.Count, i =>
                {
                    var thing = level.Things[i];
                    return new
                    {
                        index = i, thing.Num, thing.Name, thing.Template, position = Position(thing.Position),
                        orientation = new[] { thing.Pitch, thing.Yaw, thing.Roll },
                        sector = thing.Sector is null ? -1 : level.Sectors.IndexOf(thing.Sector), thing.Layer,
                        parameters = thing.Values, sourceParameters = Parameters(thing.SourceParameters),
                        thing.ArchLightingIndex, thing.ArchLightingSourceModel, thing.ArchLightingSourceModelHash,
                        architecture = AgentWorkspace.DescribeArchitecture(level, thing),
                    };
                }, i => (Match(level.Things[i].Name) || Match(level.Things[i].Template)) && (request.Filter?.Matches(level.Things[i]) ?? true));
            case "thingContainment":
                return new Catalog(level.Things.Count, i =>
                {
                    var result = ThingContainment.Evaluate(level, i);
                    return new
                    {
                        index = i, level.Things[i].Name, level.Things[i].Template,
                        position = result.Position is Vec3 p ? Position(p) : null,
                        status = result.Status.ToString(), assignment = result.Assignment.ToString(),
                        result.AssignedSectorIndex, result.AssignedSectorMatches, result.OnBoundary,
                        result.Candidates, result.Tolerance, result.Detail,
                        scope = "Point position in verified convex sectors; no collision-volume or runtime transition claim. Unsupported geometry makes candidates incomplete and therefore unavailable.",
                    };
                }, i => (Match(level.Things[i].Name) || Match(level.Things[i].Template)) && (request.Filter?.Matches(level.Things[i]) ?? true));
            case "cogs":
                return new Catalog(level.Cogs.Count, i => new { index = i, level.Cogs[i].Name,
                    values = level.Cogs[i].Values.ToArray(), bindingStatus = "unverified-use-bindings-with-explicit-resources" });
            case "lights":
                return new Catalog(level.Lights.Count, i =>
                {
                    var light = level.Lights[i];
                    return new { index = i, light.Num, light.Flags, light.Layer, position = Position(light.Position),
                        rgb = Rgb(light.Color), light.Intensity, light.Range, light.RgbIntensity,
                        light.EffectiveRgbIntensity, light.LegacyRgbRange,
                        rgbIntensityInherited = !light.RgbIntensity.HasValue };
                }, i => request.Filter?.Matches(level.Lights[i]) ?? true);
            case "architecture":
                return new Catalog(level.ArchitecturalLighting.Count, i => new {
                    index = i, meshVertexCounts = level.ArchitecturalLighting[i].Meshes.Select(m => m.Vertices.Count).ToArray(),
                    boundThings = level.Things.Select((t, index) => (t, index)).Where(p => p.t.ArchLightingIndex == i).Select(p => p.index).ToArray(),
                    samplesSha256 = Sed.Core.Lighting.ArchitecturalLightingAuthoring.SamplesDigest(level.ArchitecturalLighting[i]),
                });
            case "architecturalVertices":
                if (request.LightingIndex is not int lighting || lighting >= level.ArchitecturalLighting.Count)
                    throw new AgentRequestException("lightingIndex is outside the architecture collection.");
                var architecturalObject = level.ArchitecturalLighting[lighting];
                if (request.Mesh is not int mesh || mesh >= architecturalObject.Meshes.Count)
                    throw new AgentRequestException("mesh is outside the selected architecture object.");
                var architecturalVertices = architecturalObject.Meshes[mesh].Vertices;
                return new Catalog(architecturalVertices.Count, i => new {
                    index = i, lightingIndex = lighting, mesh, mono = architecturalVertices[i].Mono,
                    rgb = new[] { architecturalVertices[i].Red, architecturalVertices[i].Green, architecturalVertices[i].Blue },
                });
            case "templates":
                var templates = level.Templates.Values.OrderBy(t => t.Order).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                return new Catalog(templates.Length, i =>
                {
                    var template = templates[i];
                    return new { index = i, template.Name, template.Parent, template.Order,
                        parameters = template.Values, sourceParameters = Parameters(template.SourceParameters) };
                }, i => Match(templates[i].Name));
            case "materials":
                return new Catalog(document.Materials.Count, i => new { index = i, name = document.Materials[i] }, i => Match(document.Materials[i]));
            case "colormaps":
                return new Catalog(level.ColorMaps.Count, i => new { index = i, name = level.ColorMaps[i] }, i => Match(level.ColorMaps[i]));
            case "surfaces":
                var surfaceSector = SelectedSector();
                return new Catalog(surfaceSector.Surfaces.Count, i =>
                {
                    var surface = surfaceSector.Surfaces[i];
                    surface.RecalcNormal();
                    return new
                    {
                        index = i, sector = request.Sector, layer = surface.Sector.Layer, surface.Num, surface.Material, surface.MaterialIndex,
                        surface.SurfFlags, surface.FaceFlags, surface.Geo, surface.Light, surface.Tex,
                        surface.ExtraLightIntensity, surface.AdjoinFlags,
                        normal = Position(surface.Normal), geometryBounds = GeometryBounds(surface.Corners.Select(c => c.Vertex.Position)),
                        centroid = surface.Corners.Count == 0 ? null : Position(surface.Corners.Aggregate(Vec3.Zero, (sum, c) => sum + c.Vertex.Position) * (1.0 / surface.Corners.Count)),
                        adjoinSector = surface.Adjoin is null ? -1 : level.Sectors.IndexOf(surface.Adjoin.Sector),
                        adjoinSurface = surface.Adjoin is null ? -1 : surface.Adjoin.Sector.Surfaces.IndexOf(surface.Adjoin),
                        corners = surface.Corners.Select((corner, index) => new
                        {
                            index, vertex = surfaceSector.Vertices.IndexOf(corner.Vertex),
                            uv = new[] { corner.Uv.U, corner.Uv.V }, mono = corner.MonoIntensity,
                            rgb = Rgb(corner.Intensity),
                        }).ToArray(),
                    };
                }, i => request.Filter?.Matches(surfaceSector.Surfaces[i]) ?? true);
            case "vertices":
                var vertexSector = SelectedSector();
                return new Catalog(vertexSector.Vertices.Count, i => new { index = i, sector = request.Sector,
                    position = Position(vertexSector.Vertices[i].Position) });
            default:
                throw new AgentRequestException("Unsupported query collection.");
        }
    }

    private static object Summary(JklDocument document)
    {
        var level = document.Level;
        var header = level.Header;
        return new
        {
            game = level.Kind.ToString(),
            header = new
            {
                header.Version, header.Gravity,
                ceilingSky = new { header.CeilingSky.Height, offset = new[] { header.CeilingSky.Offset.X, header.CeilingSky.Offset.Y } },
                horizonSky = new { header.HorizonSky.Distance, header.HorizonSky.PixelsPerRev, offset = new[] { header.HorizonSky.Offset.X, header.HorizonSky.Offset.Y } },
                header.MipmapDistances, header.LodDistances, header.PerspectiveDistance, header.GouraudDistance,
                fog = new { header.Fog.Enabled, color = Rgb(header.Fog.Color), header.Fog.Start, header.Fog.End },
            },
            counts = new
            {
                sectors = level.Sectors.Count, surfaces = level.Sectors.Sum(s => s.Surfaces.Count),
                vertices = level.Sectors.Sum(s => s.Vertices.Count), things = level.Things.Count,
                lights = level.Lights.Count, cogs = level.Cogs.Count, architecturalObjects = level.ArchitecturalLighting.Count,
                templates = level.Templates.Count, materials = document.Materials.Count, colormaps = level.ColorMaps.Count,
            },
            level.ThingCapacity, serializedThingCapacity = System.Math.Max(level.ThingCapacity, level.Things.Count),
            preservationWarnings = document.PreservationWarnings,
        };
    }

    private static double[] Position(Vec3 value) => [value.X, value.Y, value.Z];
    private static object? GeometryBounds(IEnumerable<Vec3> vertices)
    {
        var box = Box.Empty; bool any = false;
        foreach (var vertex in vertices) { box.Encapsulate(vertex); any = true; }
        return any ? Bounds(box) : null;
    }
    private static float[] Rgb(ColorF value) => [value.R, value.G, value.B];
    private static object? Bounds(Box? value) => value is Box box ? new { min = Position(box.Min), max = Position(box.Max) } : null;
    private static object[] Parameters(IEnumerable<KeyValuePair<string, string>> records) =>
        records.Select(p => (object)new { key = p.Key, value = p.Value }).ToArray();
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

/// <summary>
/// CPU-only contract for one provenance-bound diagnostic image. The camera is perspective
/// (<see cref="FieldOfViewDegrees"/>) or orthographic (<see cref="OrthographicHeight"/> world
/// units tall); the unused one is zero. Orthographic cameras may look straight down or up,
/// which <c>sed-agent overview</c> uses for Wren's plan view.
/// </summary>
public sealed record AgentSnapshotRequest(string InputSha256, string? SourceRevision,
    Vec3 Position, Vec3 Target, double FieldOfViewDegrees, int Width, int Height,
    double NearPlane, double FarPlane)
{
    public const string PerspectiveProjection = "perspective";
    public const string OrthographicProjection = "orthographic";
    public string Mode { get; init; } = "diagnostic";
    public string LightingMode { get; init; } = "game";
    public string RenderProfile { get; init; } = "openjk";
    public bool StrictAssets { get; init; } = true;
    public IReadOnlyList<SnapshotArchive> ResourceArchives { get; init; } = Array.Empty<SnapshotArchive>();
    /// <summary><c>perspective</c> (default) or <c>orthographic</c>.</summary>
    public string Projection { get; init; } = PerspectiveProjection;
    /// <summary>World units visible vertically in an orthographic view; zero for perspective.</summary>
    public double OrthographicHeight { get; init; }
    /// <summary>Lit only: ignore lighting and show source colours (the editor's full-bright view).</summary>
    public bool Fullbright { get; init; }
    /// <summary>Lit only: draw every world surface only from in front of its plane (cutaway).</summary>
    public bool CullBackFaces { get; init; }
    public bool IsOrthographic => Projection == OrthographicProjection;
    public const int MinDimension = 64;
    public const int MaxDimension = 2048;
    public const int MaxPixels = 2_097_152;
    public const int MaxTriangles = 250_000;
    public const double MinOrthographicHeight = 0.001;
    public const double MaxOrthographicHeight = 1_000_000;

    public static object Schema()
    {
        object Hash() => new { type = "string", pattern = "^[0-9a-fA-F]{64}$" };
        object Vector() => new { type = "array", minItems = 3, maxItems = 3,
            items = new { type = "number", minimum = -1_000_000, maximum = 1_000_000 } };
        return new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "SED agent offscreen snapshot version 1",
            ["description"] = "One PNG using offscreen Vulkan without a window. Mode diagnostic uses flat geometry. Mode lit uses static world/model lighting and explicit hash-bound resource archives (first archive wins), with strict missing/unsupported asset errors by default; lit may add fullbright (ignore lighting) and cullBackFaces (draw every world surface only from in front of its plane). No dynamic lights, COG, animations, game simulation, depth or ID images. Camera is Z up and perspective (fieldOfViewDegrees) or orthographic (orthographicHeight world units tall; may look straight down or up); width*height <= 2097152 and nearPlane < farPlane. Native inputs require sourceRevision.",
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new[] { "schemaVersion", "inputSha256", "position", "target", "width", "height" },
            ["properties"] = new Dictionary<string, object>
            {
                ["schemaVersion"] = new Dictionary<string, object> { ["const"] = 1 },
                ["inputSha256"] = Hash(), ["sourceRevision"] = Hash(),
                ["position"] = Vector(), ["target"] = Vector(),
                ["projection"] = new { type = "string", @enum = new[] { PerspectiveProjection, OrthographicProjection }, @default = PerspectiveProjection },
                ["fieldOfViewDegrees"] = new { type = "number", minimum = 10, maximum = 140,
                    description = "Vertical field of view; required for perspective cameras and refused for orthographic ones." },
                ["orthographicHeight"] = new { type = "number", minimum = MinOrthographicHeight, maximum = MaxOrthographicHeight,
                    description = "World units visible vertically (the width is this times width/height); required for orthographic cameras and refused for perspective ones." },
                ["width"] = new { type = "integer", minimum = MinDimension, maximum = MaxDimension },
                ["height"] = new { type = "integer", minimum = MinDimension, maximum = MaxDimension },
                ["nearPlane"] = new { type = "number", minimum = 0.001, maximum = 1000, @default = 0.01 },
                ["farPlane"] = new { type = "number", minimum = 0.01, maximum = 100000, @default = 1000 },
                ["mode"] = new { type = "string", @enum = new[] { "diagnostic", "lit" }, @default = "diagnostic" },
                ["lightingMode"] = new { type = "string", @enum = new[] { "game", "mono", "rgb" }, @default = "game" },
                ["renderProfile"] = new { type = "string", @enum = new[] { "openjk", "palette" }, @default = "openjk",
                    description = "openjk uses palette times scalar for textured mono; palette uses CMP light rows. Neither profile establishes retail pixel parity." },
                ["strictAssets"] = new { type = "boolean", @default = true },
                ["fullbright"] = new { type = "boolean", @default = false,
                    description = "Lit only. Ignore lighting: indexed texels show their master-palette colour, solid colours their palette entry and true colour its texel. Inspects unlit levels." },
                ["cullBackFaces"] = new { type = "boolean", @default = false,
                    description = "Lit only. Draw every world surface only from in front of its plane, so ceilings and walls facing away from an outside camera vanish (cutaway). Models stay double-sided." },
                ["resourceArchives"] = ResourceManifest.Schema(),
            },
            ["allOf"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["if"] = new Dictionary<string, object>
                    {
                        ["properties"] = new Dictionary<string, object> { ["projection"] = new Dictionary<string, object> { ["const"] = OrthographicProjection } },
                        ["required"] = new[] { "projection" },
                    },
                    ["then"] = new Dictionary<string, object>
                    {
                        ["required"] = new[] { "orthographicHeight" },
                        ["not"] = new Dictionary<string, object> { ["required"] = new[] { "fieldOfViewDegrees" } },
                    },
                    ["else"] = new Dictionary<string, object>
                    {
                        ["required"] = new[] { "fieldOfViewDegrees" },
                        ["not"] = new Dictionary<string, object> { ["required"] = new[] { "orthographicHeight" } },
                    },
                },
            },
        };
    }

    public static AgentSnapshotRequest Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > AgentWorkspace.MaxRequestBytes)
            throw new AgentRequestException("Snapshot request exceeds 1 MiB.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AgentRequestException("Expected a snapshot request object.");
        string[] required = ["schemaVersion", "inputSha256", "position", "target", "width", "height"];
        string[] optional = ["sourceRevision", "projection", "fieldOfViewDegrees", "orthographicHeight", "nearPlane", "farPlane", "mode",
            "lightingMode", "renderProfile", "strictAssets", "fullbright", "cullBackFaces", "resourceArchives"];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name) || (!required.Contains(property.Name) && !optional.Contains(property.Name)))
                throw new AgentRequestException($"Duplicate or unsupported snapshot property '{property.Name}'.", jsonPointer: "/" + property.Name);
        foreach (string name in required)
            if (!seen.Contains(name)) throw new AgentRequestException($"Missing snapshot property '{name}'.", jsonPointer: "/" + name);
        var fields = new RequestFields(root);
        _ = fields.Integer("schemaVersion", 1, 1);
        int width = fields.Integer("width", MinDimension, MaxDimension), height = fields.Integer("height", MinDimension, MaxDimension);
        if ((long)width * height > MaxPixels) throw new AgentRequestException($"Snapshot exceeds the {MaxPixels}-pixel budget.", jsonPointer: "/width");
        string projection = fields.Choice("projection", PerspectiveProjection, PerspectiveProjection, OrthographicProjection);
        bool orthographic = projection == OrthographicProjection;
        if (orthographic)
        {
            if (seen.Contains("fieldOfViewDegrees"))
                throw new AgentRequestException("fieldOfViewDegrees applies only to perspective cameras.", jsonPointer: "/fieldOfViewDegrees");
            if (!seen.Contains("orthographicHeight"))
                throw new AgentRequestException("Missing snapshot property 'orthographicHeight'.", jsonPointer: "/orthographicHeight");
        }
        else
        {
            if (seen.Contains("orthographicHeight"))
                throw new AgentRequestException("orthographicHeight applies only to orthographic cameras.", jsonPointer: "/orthographicHeight");
            if (!seen.Contains("fieldOfViewDegrees"))
                throw new AgentRequestException("Missing snapshot property 'fieldOfViewDegrees'.", jsonPointer: "/fieldOfViewDegrees");
        }
        var position = fields.Vector("position"); var target = fields.Vector("target");
        var delta = target - position;
        if (orthographic)
        {
            // Parallel rays have no singular up direction: a plan view looks straight down.
            if (delta.Length < 0.000001)
                throw new AgentRequestException("Camera position and target must differ.", jsonPointer: "/target");
        }
        else if (delta.Length < 0.000001 || delta.Cross(new Vec3(0, 0, 1)).Length / delta.Length < 0.000001)
            throw new AgentRequestException("Camera position and target must differ and cannot look directly along the Z-up axis.", jsonPointer: "/target");
        double near = fields.Number("nearPlane", 0.001, 1000, 0.01), far = fields.Number("farPlane", 0.01, 100000, 1000);
        if (near >= far) throw new AgentRequestException("nearPlane must be less than farPlane.", jsonPointer: "/nearPlane");
        var mode = fields.Choice("mode", "diagnostic", "diagnostic", "lit");
        var lighting = fields.Choice("lightingMode", "game", "game", "mono", "rgb");
        var profile = fields.Choice("renderProfile", "openjk", "openjk", "palette");
        bool strict = fields.Boolean("strictAssets", true, pointer: false);
        bool fullbright = fields.Boolean("fullbright", false);
        bool cullBackFaces = fields.Boolean("cullBackFaces", false);
        var archives = new List<SnapshotArchive>();
        if (root.TryGetProperty("resourceArchives", out var archiveArray))
        {
            archives.AddRange(ResourceManifest.Parse(archiveArray).Select(a => new SnapshotArchive(a.Path, a.Sha256)));
        }
        if (mode == "lit" && archives.Count == 0) throw new AgentRequestException("Lit snapshots require resourceArchives with explicit paths and SHA256 hashes.");
        if (mode == "diagnostic" && (archives.Count > 0 || seen.Contains("lightingMode") || seen.Contains("renderProfile") || seen.Contains("strictAssets")
            || seen.Contains("fullbright") || seen.Contains("cullBackFaces")))
            throw new AgentRequestException("resourceArchives, lightingMode, renderProfile, strictAssets, fullbright and cullBackFaces apply only to lit snapshots.");
        double fieldOfView = orthographic ? 0 : fields.Number("fieldOfViewDegrees", 10, 140);
        double orthographicHeight = orthographic ? fields.Number("orthographicHeight", MinOrthographicHeight, MaxOrthographicHeight) : 0;
        return new(fields.Hash("inputSha256"), seen.Contains("sourceRevision") ? fields.Hash("sourceRevision") : null,
            position, target, fieldOfView, width, height, near, far)
            { Mode = mode, LightingMode = lighting, RenderProfile = profile, StrictAssets = strict, ResourceArchives = archives,
              Projection = projection, OrthographicHeight = orthographicHeight, Fullbright = fullbright, CullBackFaces = cullBackFaces };
    }

    /// <summary>
    /// The receipt's <c>camera</c> object, also hashed as <c>cameraSha256</c>. A perspective
    /// camera serializes exactly as before orthographic support (same members, same order), so
    /// existing requests keep their camera digests. Shared by the runner and MCP evidence checks.
    /// </summary>
    public object CameraDescription() => IsOrthographic
        ? new
        {
            position = new[] { Position.X, Position.Y, Position.Z },
            target = new[] { Target.X, Target.Y, Target.Z },
            OrthographicHeight, NearPlane, FarPlane, up = "Z", projection = OrthographicProjection,
        }
        : new
        {
            position = new[] { Position.X, Position.Y, Position.Z },
            target = new[] { Target.X, Target.Y, Target.Z },
            FieldOfViewDegrees, NearPlane, FarPlane, up = "Z", projection = PerspectiveProjection,
        };

    /// <summary>
    /// Canonical request JSON: <c>Parse(ToJson().ToJsonString())</c> gives back the same request.
    /// Lit-only fields appear only for lit requests, since a diagnostic request refuses them;
    /// <c>projection</c> appears only when orthographic. <c>sed-agent overview</c> embeds this in
    /// its receipt so <c>snapshot</c> can reproduce the overview's camera and pixels.
    /// </summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["schemaVersion"] = 1, ["inputSha256"] = InputSha256 };
        if (SourceRevision is not null) json["sourceRevision"] = SourceRevision;
        json["position"] = new JsonArray(Position.X, Position.Y, Position.Z);
        json["target"] = new JsonArray(Target.X, Target.Y, Target.Z);
        if (IsOrthographic)
        {
            json["projection"] = OrthographicProjection;
            json["orthographicHeight"] = OrthographicHeight;
        }
        else json["fieldOfViewDegrees"] = FieldOfViewDegrees;
        json["width"] = Width; json["height"] = Height;
        json["nearPlane"] = NearPlane; json["farPlane"] = FarPlane;
        json["mode"] = Mode;
        if (Mode == "lit")
        {
            json["lightingMode"] = LightingMode; json["renderProfile"] = RenderProfile; json["strictAssets"] = StrictAssets;
            json["fullbright"] = Fullbright; json["cullBackFaces"] = CullBackFaces;
            json["resourceArchives"] = new JsonArray(ResourceArchives
                .Select(archive => (JsonNode)new JsonObject { ["path"] = archive.Path, ["sha256"] = archive.Sha256 }).ToArray());
        }
        return json;
    }

    public void VerifySource(AgentDocumentSnapshot snapshot) => VerifySource(InputSha256, SourceRevision, snapshot);

    /// <summary>The source/revision guard shared by snapshot and overview requests.</summary>
    public static void VerifySource(string inputSha256, string? sourceRevision, AgentDocumentSnapshot snapshot)
    {
        if (!string.Equals(inputSha256, snapshot.InputSha256, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException("Snapshot input changed since inspection. Inspect again before rendering.");
        if (snapshot.Dependencies.Count != 0 && sourceRevision is null)
            throw new AgentRequestException("Native snapshots require sourceRevision from inspect/query.", jsonPointer: "/sourceRevision");
        if (sourceRevision is not null && !string.Equals(sourceRevision, snapshot.SourceRevision, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException("Snapshot source or native template dependency changed since inspection.");
    }

    /// <summary>Fresh destination preflight; callers must still use an atomic no-clobber final move.</summary>
    public static string PrepareOutputPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new AgentRequestException("Snapshot output must have the .png extension.");
        if (File.Exists(path) || Directory.Exists(path)) throw new AgentConflictException("Snapshot output already exists; choose a new file.");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new AgentRequestException("Snapshot output directory must exist.");
        return path;
    }

    /// <summary>Diagnostic policy: reciprocal visible portals without a drawable face are openings.</summary>
    public static bool IsOpenPortal(Surface surface) => surface.Adjoin is { } other
        && ReferenceEquals(other.Adjoin, surface)
        && (surface.AdjoinFlags & AdjoinFlags.Visible) != 0
        && (surface.Geo == 0 || string.IsNullOrWhiteSpace(surface.Material));
}

public sealed record SnapshotArchive(string Path, string Sha256) : ResourceArchiveRequest(Path, Sha256);

/// <summary>Typed readers for one JSON request object, with the snapshot contract's error texts.</summary>
internal readonly struct RequestFields(JsonElement root)
{
    public int Integer(string key, int min, int max)
    {
        var value = root.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int n) || n < min || n > max)
            throw new AgentRequestException($"{key} must be an integer from {min} through {max}.", jsonPointer: "/" + key);
        return n;
    }

    public double Number(string key, double min, double max, double? fallback = null)
    {
        if (!root.TryGetProperty(key, out var value) && fallback is double f) return f;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n) || n < min || n > max)
            throw new AgentRequestException($"{key} must be finite and from {min} through {max}.", jsonPointer: "/" + key);
        return n;
    }

    public Vec3 Vector(string key)
    {
        var value = root.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            throw new AgentRequestException($"{key} must contain three coordinates.", jsonPointer: "/" + key);
        var coordinates = new double[3];
        int i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double n) || !double.IsFinite(n) || System.Math.Abs(n) > 1_000_000)
                throw new AgentRequestException($"{key} contains an invalid coordinate.", jsonPointer: $"/{key}/{i}");
            coordinates[i++] = n;
        }
        return new Vec3(coordinates[0], coordinates[1], coordinates[2]);
    }

    public string Hash(string key)
    {
        var value = root.GetProperty(key);
        string? hash = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (hash is null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
            throw new AgentRequestException($"{key} must be a 64-digit SHA256 from inspect/query.", jsonPointer: "/" + key);
        return hash.ToLowerInvariant();
    }

    public string Choice(string key, string fallback, params string[] choices)
    {
        if (!root.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.String || !choices.Contains(value.GetString()))
            throw new AgentRequestException($"{key} must be one of: {string.Join(", ", choices)}.", jsonPointer: "/" + key);
        return value.GetString()!;
    }

    /// <summary>A JSON boolean, or the fallback when absent. <paramref name="pointer"/> keeps the historical pointer-free strictAssets error.</summary>
    public bool Boolean(string key, bool fallback, bool pointer = true)
    {
        if (!root.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new AgentRequestException($"{key} must be a boolean.", jsonPointer: pointer ? "/" + key : null);
        return value.GetBoolean();
    }
}

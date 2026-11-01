using System.Text;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

/// <summary>
/// Request for <c>sed-agent overview</c>: Wren's level-preview cameras as a lit SED snapshot.
/// Wren (a Grok bot, one of Andrei's AI collaborators) previewed levels with an auto-framed
/// orthographic overhead plan and a three-quarter perspective from the south-east, drawn as a
/// backface-culled "doll's house"; recovered 2026-10-31. The caller picks a view and an image
/// size; the camera is computed from the level's drawn world surfaces (SPEC §2.1, "Wren
/// framing"), and the receipt embeds the exact equivalent snapshot request.
/// </summary>
public sealed record AgentOverviewRequest(string InputSha256, string? SourceRevision, string View, int Width, int Height)
{
    public const string OverheadView = "overhead";
    public const string ThreeQuarterView = "threequarter";
    public const string WrenFraming = "wren";
    public const string ContainFraming = "contain";
    public const double DefaultMargin = 0.025;
    public const double MaximumMargin = 0.25;
    /// <summary><c>wren</c> (Wren's fixed distance; may crop near corners) or <c>contain</c> (back off until the box fits).</summary>
    public string Framing { get; init; } = WrenFraming;
    /// <summary>Fraction of the image kept free on each side (0 through 0.25).</summary>
    public double Margin { get; init; } = DefaultMargin;
    public string LightingMode { get; init; } = "game";
    public string RenderProfile { get; init; } = "openjk";
    public bool StrictAssets { get; init; } = true;
    public bool Fullbright { get; init; }
    /// <summary>Wren's "doll's house": world surfaces drawn only from in front. On by default for overviews.</summary>
    public bool CullBackFaces { get; init; } = true;
    public IReadOnlyList<SnapshotArchive> ResourceArchives { get; init; } = Array.Empty<SnapshotArchive>();

    public static object Schema()
    {
        object Hash() => new { type = "string", pattern = "^[0-9a-fA-F]{64}$" };
        return new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "SED agent Wren-style overview version 1",
            ["description"] = "One static lit PNG framed automatically on the level's drawn world surfaces (geo != 0, three or more corners, a material; sky included, things excluded), after Wren's level previews: view overhead is an orthographic plan looking straight down with north (+Y) up; view threequarter is a perspective from the south-east and above (direction 0.7, -0.7, 0.6; Blender lens 35 mm on a 36 mm sensor). framing wren keeps Wren's fixed distance (0.75 of the box diagonal), which may crop near corners; contain backs off until the whole box fits inside the margin. The receipt embeds the equivalent snapshot request (overview.snapshotRequest). Resources, limits and lit options are those of snapshot; cullBackFaces defaults to true here.",
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new[] { "schemaVersion", "inputSha256", "view", "width", "height", "resourceArchives" },
            ["properties"] = new Dictionary<string, object>
            {
                ["schemaVersion"] = new Dictionary<string, object> { ["const"] = 1 },
                ["inputSha256"] = Hash(), ["sourceRevision"] = Hash(),
                ["view"] = new { type = "string", @enum = new[] { OverheadView, ThreeQuarterView } },
                ["width"] = new { type = "integer", minimum = AgentSnapshotRequest.MinDimension, maximum = AgentSnapshotRequest.MaxDimension },
                ["height"] = new { type = "integer", minimum = AgentSnapshotRequest.MinDimension, maximum = AgentSnapshotRequest.MaxDimension },
                ["framing"] = new { type = "string", @enum = new[] { WrenFraming, ContainFraming }, @default = WrenFraming },
                ["margin"] = new { type = "number", minimum = 0, maximum = MaximumMargin, @default = DefaultMargin,
                    description = "Fraction of the image kept free on each side. Overhead always applies it; threequarter applies it only with contain framing." },
                ["lightingMode"] = new { type = "string", @enum = new[] { "game", "mono", "rgb" }, @default = "game" },
                ["renderProfile"] = new { type = "string", @enum = new[] { "openjk", "palette" }, @default = "openjk" },
                ["strictAssets"] = new { type = "boolean", @default = true },
                ["fullbright"] = new { type = "boolean", @default = false },
                ["cullBackFaces"] = new { type = "boolean", @default = true },
                ["resourceArchives"] = ResourceManifest.Schema(),
            },
        };
    }

    public static AgentOverviewRequest Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > AgentWorkspace.MaxRequestBytes)
            throw new AgentRequestException("Overview request exceeds 1 MiB.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AgentRequestException("Expected an overview request object.");
        string[] required = ["schemaVersion", "inputSha256", "view", "width", "height", "resourceArchives"];
        string[] optional = ["sourceRevision", "framing", "margin", "lightingMode", "renderProfile", "strictAssets", "fullbright", "cullBackFaces"];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name) || (!required.Contains(property.Name) && !optional.Contains(property.Name)))
                throw new AgentRequestException($"Duplicate or unsupported overview property '{property.Name}'.", jsonPointer: "/" + property.Name);
        foreach (string name in required)
            if (!seen.Contains(name))
                throw name == "resourceArchives"
                    ? new AgentRequestException("Overview requires resourceArchives with explicit paths and SHA256 hashes.", jsonPointer: "/resourceArchives")
                    : new AgentRequestException($"Missing overview property '{name}'.", jsonPointer: "/" + name);
        var fields = new RequestFields(root);
        _ = fields.Integer("schemaVersion", 1, 1);
        int width = fields.Integer("width", AgentSnapshotRequest.MinDimension, AgentSnapshotRequest.MaxDimension);
        int height = fields.Integer("height", AgentSnapshotRequest.MinDimension, AgentSnapshotRequest.MaxDimension);
        if ((long)width * height > AgentSnapshotRequest.MaxPixels)
            throw new AgentRequestException($"Overview exceeds the {AgentSnapshotRequest.MaxPixels}-pixel budget.", jsonPointer: "/width");
        string view = fields.Choice("view", "", OverheadView, ThreeQuarterView);
        string framing = fields.Choice("framing", WrenFraming, WrenFraming, ContainFraming);
        double margin = fields.Number("margin", 0, MaximumMargin, DefaultMargin);
        string lighting = fields.Choice("lightingMode", "game", "game", "mono", "rgb");
        string profile = fields.Choice("renderProfile", "openjk", "openjk", "palette");
        bool strict = fields.Boolean("strictAssets", true);
        bool fullbright = fields.Boolean("fullbright", false);
        bool cull = fields.Boolean("cullBackFaces", true);
        var archiveArray = root.GetProperty("resourceArchives");
        if (archiveArray.ValueKind == JsonValueKind.Array && archiveArray.GetArrayLength() == 0)
            throw new AgentRequestException("Overview requires resourceArchives with explicit paths and SHA256 hashes.", jsonPointer: "/resourceArchives");
        var archives = ResourceManifest.Parse(archiveArray).Select(a => new SnapshotArchive(a.Path, a.Sha256)).ToList();
        return new(fields.Hash("inputSha256"), seen.Contains("sourceRevision") ? fields.Hash("sourceRevision") : null, view, width, height)
        {
            Framing = framing, Margin = margin, LightingMode = lighting, RenderProfile = profile, StrictAssets = strict,
            Fullbright = fullbright, CullBackFaces = cull, ResourceArchives = archives,
        };
    }

    public void VerifySource(AgentDocumentSnapshot snapshot) => AgentSnapshotRequest.VerifySource(InputSha256, SourceRevision, snapshot);

    /// <summary>
    /// The lit snapshot request equivalent to this overview with a computed camera. It is built
    /// as canonical JSON and parsed by the snapshot contract itself, so it is exactly what
    /// <c>snapshot</c> would accept; a camera outside the snapshot limits is refused here.
    /// </summary>
    public AgentSnapshotRequest ToSnapshotRequest(Vec3 position, Vec3 target, string projection, double fieldOfViewDegrees,
        double orthographicHeight, double nearPlane, double farPlane)
    {
        bool orthographic = projection == AgentSnapshotRequest.OrthographicProjection;
        if (!orthographic && projection != AgentSnapshotRequest.PerspectiveProjection)
            throw new ArgumentException("projection must be perspective or orthographic.", nameof(projection));
        var draft = new AgentSnapshotRequest(InputSha256, SourceRevision, position, target, orthographic ? 0 : fieldOfViewDegrees,
            Width, Height, nearPlane, farPlane)
        {
            Mode = "lit", LightingMode = LightingMode, RenderProfile = RenderProfile, StrictAssets = StrictAssets,
            ResourceArchives = ResourceArchives, Projection = projection, OrthographicHeight = orthographic ? orthographicHeight : 0,
            Fullbright = Fullbright, CullBackFaces = CullBackFaces,
        };
        string json = draft.ToJson().ToJsonString();
        try { return AgentSnapshotRequest.Parse(json); }
        catch (AgentRequestException error)
        {
            throw new AgentRequestException($"Computed overview camera is outside the snapshot limits: {error.Message.TrimEnd('.')}.", jsonPointer: error.JsonPointer);
        }
    }
}

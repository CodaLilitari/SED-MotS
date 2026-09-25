using System.Text;
using System.Text.Json;
using Sed.Core.Lighting;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    /// <summary>Bounded read-only explanation of the shared world's corner bake.</summary>
    public static object DiagnoseLighting(string path, string requestText)
    {
        if (Encoding.UTF8.GetByteCount(requestText) > MaxRequestBytes)
            throw new AgentRequestException("Lighting request exceeds 1 MiB.");
        using var json = JsonDocument.Parse(requestText, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        FieldsWithOptional(root, ["sector", "offset", "limit", "castShadows", "updateSectorAmbients"], "schemaVersion");
        if (Integer(root, "schemaVersion") != SchemaVersion) throw new AgentRequestException("Unsupported lighting schemaVersion.");
        int offset = root.TryGetProperty("offset", out _) ? Integer(root, "offset") : 0;
        int limit = root.TryGetProperty("limit", out _) ? Integer(root, "limit") : AgentQueries.DefaultLimit;
        if (offset < 0 || limit is < 1 or > AgentQueries.MaxLimit)
            throw new AgentRequestException("offset must be nonnegative and limit must be 1 through 200.");
        bool Boolean(string key) => !root.TryGetProperty(key, out var value) ? true
            : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean()
            : throw FieldError($"{key} must be a boolean.", key);
        var snapshot = LoadWithProvenance(path);
        var level = snapshot.Document.Level;
        int? sector = root.TryGetProperty("sector", out _) ? Integer(root, "sector") : null;
        var targets = sector is int selected ? new[] { At(level.Sectors, selected, "sector") } : null;
        var options = new LightingOptions { CastShadows = Boolean("castShadows"), UpdateSectorAmbients = Boolean("updateSectorAmbients") };
        int[] indices = Enumerable.Range(0, level.Lights.Count).Skip(offset).Take(limit).ToArray();
        var diagnostics = LightCalculator.Diagnose(level, targets, options, indices);
        var response = new
        {
            schemaVersion = 1, snapshot.InputSha256, snapshot.SourceRevision, snapshot.Dependencies,
            game = diagnostics.Game.ToString(), diagnostics.CastShadows, diagnostics.UpdateSectorAmbients,
            diagnostics.TargetSectorIndices, diagnostics.TargetSurfaces, diagnostics.CandidateSamples, diagnostics.GeometryIssue,
            total = level.Lights.Count, offset, limit, returnedCount = indices.Length,
            nextOffset = (long)offset + indices.Length < level.Lights.Count ? (int?)(offset + indices.Length) : null,
            items = diagnostics.Lights.Select(light => new { light.LightIndex, status = light.Status.ToString(),
                light.BakerSectorIndex, light.Samples, light.Detail }).ToArray(),
            scope = "World surface corners using the shared editor bake predicates and options. No write, ambient prediction, ARCH/model sampling, final accumulated light, runtime or retail proof. Counts are corners, not unique vertices; unavailable counts are null. Each page scans geometry and its selected lights.",
        };
        if (JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions).Length > AgentQueries.MaxResponseBytes)
            throw new AgentRequestException("Lighting diagnostics exceed 1 MiB. Request a smaller page or one sector.");
        return response;
    }

    public static object LightingRequestSchema() => new Dictionary<string, object>
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object", ["additionalProperties"] = false,
        ["description"] = "Read-only per-light world bake explanation, paginated by light list index. Optional sector limits target corners. Default options match the editor baker; no mutation or ambient recomputation.",
        ["required"] = new[] { "schemaVersion" },
        ["properties"] = new Dictionary<string, object>
        {
            ["schemaVersion"] = new Dictionary<string, object> { ["const"] = 1 },
            ["sector"] = new { type = "integer", minimum = 0, maximum = int.MaxValue },
            ["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue, @default = 0 },
            ["limit"] = new { type = "integer", minimum = 1, maximum = AgentQueries.MaxLimit, @default = AgentQueries.DefaultLimit },
            ["castShadows"] = new { type = "boolean", @default = true },
            ["updateSectorAmbients"] = new { type = "boolean", @default = true },
        },
    };
}

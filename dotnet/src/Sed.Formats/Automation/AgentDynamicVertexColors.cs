using System.Text;
using System.Text.Json;
using Sed.Formats.Jkl;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    public static object DiagnoseVertexColors(string path, string requestText)
    {
        if (Encoding.UTF8.GetByteCount(requestText) > MaxRequestBytes)
            throw new AgentRequestException("Vertex-color request exceeds 1 MiB.");
        using var json = JsonDocument.Parse(requestText, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        FieldsWithOptional(root, ["sector", "offset", "limit"], "schemaVersion");
        if (Integer(root, "schemaVersion") != SchemaVersion) throw new AgentRequestException("Unsupported vertex-color schemaVersion.");
        int offset = root.TryGetProperty("offset", out _) ? Integer(root, "offset") : 0;
        int limit = root.TryGetProperty("limit", out _) ? Integer(root, "limit") : AgentQueries.DefaultLimit;
        if (offset < 0 || limit is < 1 or > AgentQueries.MaxLimit)
            throw new AgentRequestException("offset must be nonnegative and limit must be 1 through 200.");
        var snapshot = LoadWithProvenance(path);
        int? sector = root.TryGetProperty("sector", out _) ? Integer(root, "sector") : null;
        if (sector is int selected) _ = At(snapshot.Document.Level.Sectors, selected, "sector");
        var report = JklDynamicVertexColors.Evaluate(snapshot.Document);
        var candidates = report.Surfaces.Where(s => sector is null || s.SectorIndex == sector).ToArray();
        var items = candidates.Skip(offset).Take(limit).ToArray();
        EnsureInputsUnchanged(path, snapshot.InputSha256, snapshot.Dependencies);
        var response = new
        {
            schemaVersion = SchemaVersion, snapshot.InputSha256, snapshot.SourceRevision, snapshot.Dependencies,
            report.Status, report.ProposedOutputSha256, report.GeometryValid, report.Detail,
            total = candidates.Length, sector, offset, limit, returnedCount = items.Length,
            nextOffset = (long)offset + items.Length < candidates.Length ? (int?)(offset + items.Length) : null,
            items,
        };
        if (JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions).Length > AgentQueries.MaxResponseBytes)
            throw new AgentRequestException("Vertex-color diagnostics exceed 1 MiB; request a smaller page.");
        return response;
    }

    public static object VertexColorsRequestSchema() => new Dictionary<string, object>
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object", ["additionalProperties"] = false,
        ["description"] = "Read-only MotS dynamic vertex-color storage inference against actual proposed serialized output. Optional sector and surface pagination. No preparation, COG, visibility, networking or retail runtime proof.",
        ["required"] = new[] { "schemaVersion" },
        ["properties"] = new Dictionary<string, object>
        {
            ["schemaVersion"] = new { @const = 1 },
            ["sector"] = new { type = "integer", minimum = 0, maximum = int.MaxValue },
            ["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue, @default = 0 },
            ["limit"] = new { type = "integer", minimum = 1, maximum = AgentQueries.MaxLimit, @default = AgentQueries.DefaultLimit },
        },
    };
}

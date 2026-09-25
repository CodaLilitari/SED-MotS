using System.Text.Json;
using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    public static object DiagnoseAssets(string path, string requestText)
    {
        var snapshot = LoadWithProvenance(path);
        var options = ParseExportResources(requestText, snapshot, "offset", "limit", "maxRows", "maxWork", "assetResourceRevision")!;
        using var json = JsonDocument.Parse(requestText);
        var root = json.RootElement;
        if (root.TryGetProperty("resourceRevision", out _))
            throw new AgentRequestException("assets uses assetResourceRevision for this scan. Patch resourceRevision must come from the reviewed plan.");
        int Read(string name, int fallback) => root.TryGetProperty(name, out _) ? Integer(root, name) : fallback;
        int offset = Read("offset", 0), limit = Read("limit", AgentQueries.DefaultLimit);
        int maxRows = Read("maxRows", 10000), maxWork = Read("maxWork", 100000);
        if (offset < 0 || limit is < 1 or > AgentQueries.MaxLimit || maxRows is < 1 or > 100000 || maxWork is < 1 or > 1000000)
            throw new AgentRequestException("Invalid asset page or scan budget. Use schema assets for bounds.");
        using var resources = new ExplicitArchiveResources(options.Archives);
        var report = LevelAssetDiagnostics.Describe(snapshot.Document, resources, maxRows, maxWork);
        resources.VerifyUnchanged();
        if (root.TryGetProperty("assetResourceRevision", out _) &&
            ResourceManifest.NormalizeHash(String(root, "assetResourceRevision"), "assetResourceRevision") != report.ResourceRevision)
            throw new AgentConflictException("The asset scan's resource revision changed. Review the same scan budgets and current archives again.");
        var selected = report.Rows.Skip(offset).Take(limit).ToArray();
        object Page(int count) => new
        {
            schemaVersion = 1, snapshot.InputSha256, snapshot.SourceRevision, snapshot.Dependencies,
            assetResourceRevision = report.ResourceRevision, report.Archives, report.Scope, report.DirectScanComplete,
            report.RecursiveClosureVerified, report.ResolutionSemantics,
            report.WorkUsed, report.MaxRows, report.MaxWork, report.Diagnostics, report.UnverifiedScopes,
            total = report.Rows.Count, offset, limit, returnedCount = count,
            nextOffset = (long)offset + count < report.Rows.Count ? (int?)(offset + count) : null,
            rows = selected.Take(count).ToArray(),
        };
        for (int count = selected.Length; count >= 0; count--)
        {
            if (count == 0 && selected.Length > 0) break;
            var page = Page(count);
            if (JsonSerializer.SerializeToUtf8Bytes(page, JsonOptions).Length <= AgentQueries.MaxResponseBytes) return page;
        }
        throw new AgentRequestException("Asset row or diagnostics exceed 1 MiB; request a smaller scan budget. Fields were not truncated.");
    }

    public static object AssetsRequestSchema()
    {
        var schema = (Dictionary<string, object>)ResourceRequestSchema();
        schema["description"] = "Known direct level resource references resolved in explicit archives, with exact origins and receipts. Not recursive resource closure or runtime loading. Each page repeats a bounded direct scan; inspect directScanComplete and unverifiedScopes. Scan budgets can be increased explicitly.";
        var properties = (Dictionary<string, object>)schema["properties"];
        properties.Remove("resourceRevision");
        properties["assetResourceRevision"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$",
            description = "Optional identity for this same direct scan and budgets. It is not a patch resourceRevision; copy that from the reviewed patch plan." };
        properties["offset"] = new { type = "integer", minimum = 0, maximum = int.MaxValue, @default = 0 };
        properties["limit"] = new { type = "integer", minimum = 1, maximum = AgentQueries.MaxLimit, @default = AgentQueries.DefaultLimit };
        properties["maxRows"] = new { type = "integer", minimum = 1, maximum = 100000, @default = 10000 };
        properties["maxWork"] = new { type = "integer", minimum = 1, maximum = 1000000, @default = 100000 };
        return schema;
    }
}

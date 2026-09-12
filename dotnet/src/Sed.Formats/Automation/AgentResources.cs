using System.Text;
using System.Text.Json;
using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

internal sealed record AgentResourceOptions(IReadOnlyList<ResourceArchiveRequest> Archives, string? ExpectedRevision);
internal sealed record AgentResourceCapture(IReadOnlyList<ResourceArchiveRequest> Archives, string Revision,
    IReadOnlyList<string> Models, string OutputSha256);

public static partial class AgentWorkspace
{
    private static AgentResourceOptions? ParseResourceOptions(JsonElement root)
    {
        if (!root.TryGetProperty("resourceArchives", out var archives))
        {
            if (root.TryGetProperty("resourceRevision", out _))
                throw new AgentRequestException("resourceRevision requires resourceArchives.", jsonPointer: "/resourceRevision");
            return null;
        }
        string? revision = root.TryGetProperty("resourceRevision", out _)
            ? ResourceManifest.NormalizeHash(String(root, "resourceRevision"), "resourceRevision") : null;
        return new(ResourceManifest.Parse(archives), revision);
    }

    private static AgentResourceOptions? ParseExportResources(string? text, AgentDocumentSnapshot snapshot)
    {
        if (text is null) return null;
        if (Encoding.UTF8.GetByteCount(text) > MaxRequestBytes) throw new AgentRequestException("Resource request exceeds 1 MiB.");
        using var request = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = request.RootElement;
        FieldsWithOptional(root, ["sourceRevision", "resourceRevision"], "schemaVersion", "inputSha256", "resourceArchives");
        if (Integer(root, "schemaVersion") != SchemaVersion) throw new AgentRequestException("Unsupported resource schemaVersion.");
        var hash = ResourceManifest.NormalizeHash(String(root, "inputSha256"), "inputSha256");
        if (hash != snapshot.InputSha256) throw new AgentConflictException("The source changed since inspection. Regenerate the resource request.");
        if (snapshot.Dependencies.Count != 0 && !root.TryGetProperty("sourceRevision", out _))
            throw new AgentRequestException("Native resource validation requires sourceRevision.", jsonPointer: "/sourceRevision");
        if (root.TryGetProperty("sourceRevision", out _)
            && ResourceManifest.NormalizeHash(String(root, "sourceRevision"), "sourceRevision") != snapshot.SourceRevision)
            throw new AgentConflictException("The source or native template dependency changed. Regenerate the resource request.");
        return ParseResourceOptions(root);
    }

    private static void CheckResourceRevision(AgentResourceOptions? options, string? revision)
    {
        if (options?.ExpectedRevision is { } expected && expected != revision)
            throw new AgentConflictException("Resource revision differs from the reviewed validation. Revalidate the current archives and regenerate the request. No output was written.");
    }

    private static AgentResourceCapture? CaptureResources(AgentResourceOptions? options,
        ArchitectureResourceValidationResult validation, string output)
        => options is null ? null : new(Array.AsReadOnly(options.Archives.ToArray()), validation.ResourceRevision!,
            Array.AsReadOnly(validation.UsedResources.Select(e => e.Requested).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            Hash(Encoding.UTF8.GetBytes(output)));

    private static ExplicitArchiveResources? ReopenCapturedResources(PreparedAgentEdit edit)
    {
        if (edit.ResourceCapture is not { } capture) return null;
        if (Hash(Encoding.UTF8.GetBytes(edit.OutputText)) != capture.OutputSha256)
            throw new AgentConflictException("Prepared output differs from the resource-validated document. Generate a fresh plan.");
        var resources = new ExplicitArchiveResources(capture.Archives);
        try
        {
            foreach (string model in capture.Models) resources.ResolveArchitectureModel(model);
            if (resources.ResourceRevision != capture.Revision)
                throw new AgentConflictException("Resource resolution changed after planning. No output was written.");
            return resources;
        }
        catch { resources.Dispose(); throw; }
    }

    public static object ResourceRequestSchema() => new Dictionary<string, object> {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object", ["additionalProperties"] = false,
        ["description"] = "Current all-LOD architectural model validation. Native historical fingerprints are checked when present; JKL has no historical bake identity. Other asset closure and gameplay are outside scope.",
        ["required"] = new[] { "schemaVersion", "inputSha256", "resourceArchives" },
        ["properties"] = new Dictionary<string, object> {
            ["schemaVersion"] = new Dictionary<string, object> { ["const"] = 1 },
            ["inputSha256"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$" },
            ["sourceRevision"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$", description = "Required for native projects." },
            ["resourceRevision"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$", description = "Copy the reviewed resourceRevision to refuse changed archive order, bytes or model resolution." },
            ["resourceArchives"] = ResourceManifest.Schema() } };
}

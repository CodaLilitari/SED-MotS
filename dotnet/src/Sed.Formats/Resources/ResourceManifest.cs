using System.Text.Json;
using Sed.Formats.Automation;

namespace Sed.Formats.Resources;

/// <summary>Common explicit-archive contract for editing, conversion and offscreen rendering.</summary>
public static class ResourceManifest
{
    public static string NormalizeHash(string? value, string field)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new AgentRequestException($"{field} must contain 64 hex digits.");
        return value.ToLowerInvariant();
    }

    public static IReadOnlyList<ResourceArchiveRequest> Parse(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 16)
            throw new AgentRequestException("resourceArchives must contain 1 through 16 explicit archives.");
        var requests = new List<ResourceArchiveRequest>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 2
                || !entry.TryGetProperty("path", out var pathValue) || pathValue.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("sha256", out var hashValue) || hashValue.ValueKind != JsonValueKind.String)
                throw new AgentRequestException("Each resource archive requires only path and sha256.");
            string path = pathValue.GetString()!;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new AgentRequestException("Resource archive paths must be explicit absolute paths.");
            path = Path.GetFullPath(path);
            if (!paths.Add(path)) throw new AgentRequestException("Duplicate resource archive path.");
            requests.Add(new(path, NormalizeHash(hashValue.GetString(), "resource archive sha256")));
        }
        return requests.AsReadOnly();
    }

    public static object Schema() => new { type = "array", minItems = 1, maxItems = 16, items = new {
        type = "object", additionalProperties = false, required = new[] { "path", "sha256" },
        properties = new Dictionary<string, object> { ["path"] = new { type = "string", minLength = 1 },
            ["sha256"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$" } } } };
}

using System.Text;
using System.Text.Json;
using Sed.Core.IO;
using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

/// <summary>Privately captured package bytes and source identities; callers can review metadata only.</summary>
public sealed class PreparedAgentPackage
{
    public string InputPath { get; }
    public string InputSha256 => Package.InputSha256;
    public string? OutputSha256 => Package.OutputSha256;
    public string ManifestSha256 => Package.ManifestSha256;
    public string ArchiveExtension => Package.ArchiveExtension;
    public bool CanBuild => Package.CanBuild;
    public JsonElement Report => Package.Report;
    internal PreparedLevelPackage Package { get; }
    internal IReadOnlyList<ResourceArchiveRequest> Archives { get; }

    internal PreparedAgentPackage(string inputPath, PreparedLevelPackage package,
        IReadOnlyList<ResourceArchiveRequest> archives)
    {
        InputPath = inputPath;
        Package = package;
        Archives = Array.AsReadOnly(archives.Select(a => new ResourceArchiveRequest(a.Path, a.Sha256)).ToArray());
    }
}

public static partial class AgentWorkspace
{
    public static object PackageRequestSchema() => new
    {
        type = "object", additionalProperties = false,
        required = new[] { "schemaVersion", "inputSha256", "levelFileName" },
        properties = new Dictionary<string, object>
        {
            ["schemaVersion"] = new { type = "integer", @const = 1 },
            ["inputSha256"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$" },
            ["levelFileName"] = new { type = "string", maxLength = 31, pattern = "^[A-Za-z0-9_-]{1,27}\\.[Jj][Kk][Ll]$" },
            ["resourceArchives"] = new { type = "array", maxItems = 16, items = new {
                type = "object", additionalProperties = false, required = new[] { "path", "sha256" },
                properties = new Dictionary<string, object> {
                    ["path"] = new { type = "string", minLength = 1, description = "Absolute GOB/GOO path; caller order is provider precedence." },
                    ["sha256"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$" } } } },
            ["stockArchives"] = new { type = "array", maxItems = 16, uniqueItems = true,
                items = new { type = "string", minLength = 1 },
                description = "Absolute paths from resourceArchives whose resolved resources should be omitted. This is a caller declaration, not stock or license verification." },
            ["episodeTitle"] = new { type = "string", minLength = 1, maxLength = 80, @default = "SED package",
                description = "Printable ASCII without quotes or #." },
            ["episodeType"] = new { type = "integer", @enum = new[] { 1, 2 }, @default = 1 },
            ["extraResources"] = new { type = "array", maxItems = 256, items = new {
                type = "object", additionalProperties = false, required = new[] { "name", "folder" },
                properties = new Dictionary<string, object> {
                    ["name"] = new { type = "string", minLength = 1, maxLength = 127,
                        description = "Relative ASCII resource name; folder plus slash plus name must fit 127 bytes." },
                    ["folder"] = new { type = "string", @enum = PackageResourcePath.Folders } } } },
            ["allowUnverifiedReferences"] = new { type = "boolean", @default = false,
                description = "Acknowledge disclosed runtime or unsupported-reference uncertainty. Errors and limits still block." },
            ["maxNodes"] = new { type = "integer", minimum = 1, maximum = 4096, @default = 4096 },
            ["maxEdges"] = new { type = "integer", minimum = 1, maximum = 16384, @default = 16384 },
            ["maxDepth"] = new { type = "integer", minimum = 0, maximum = 32, @default = 32 },
            ["maxPackageBytes"] = new { type = "integer", minimum = 1, maximum = LevelPackageOptions.HardMaxPackageBytes,
                @default = LevelPackageOptions.HardMaxPackageBytes }
        }
    };

    public static PreparedAgentPackage PlanPackage(string path, string requestText)
    {
        if (!Path.GetExtension(path).Equals(".jkl", StringComparison.OrdinalIgnoreCase))
            throw new AgentRequestException("Reviewed packages require a saved JKL. Convert and review native projects before packaging.");
        if (Encoding.UTF8.GetByteCount(requestText) > MaxRequestBytes)
            throw new AgentRequestException("Package request exceeds 1 MiB.");
        using var json = JsonDocument.Parse(requestText, new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        FieldsWithOptional(root, ["resourceArchives", "stockArchives", "episodeTitle", "episodeType", "extraResources",
            "allowUnverifiedReferences", "maxNodes", "maxEdges", "maxDepth", "maxPackageBytes"],
            "schemaVersion", "inputSha256", "levelFileName");
        if (Integer(root, "schemaVersion") != 1) throw FieldError("Unsupported package schemaVersion.", "schemaVersion");
        string expectedInput = ResourceManifest.NormalizeHash(String(root, "inputSha256"), "inputSha256");
        IReadOnlyList<ResourceArchiveRequest> archives = [];
        if (root.TryGetProperty("resourceArchives", out var archiveArray))
        {
            if (archiveArray.ValueKind != JsonValueKind.Array)
                throw FieldError("resourceArchives must be an array.", "resourceArchives");
            if (archiveArray.GetArrayLength() > 0) archives = ResourceManifest.Parse(archiveArray);
        }
        var stock = new List<string>();
        if (root.TryGetProperty("stockArchives", out var stockArray))
        {
            if (stockArray.ValueKind != JsonValueKind.Array || stockArray.GetArrayLength() > 16)
                throw FieldError("stockArchives must be an array of at most 16 paths.", "stockArchives");
            foreach (var item in stockArray.EnumerateArray())
                stock.Add(item.ValueKind == JsonValueKind.String ? item.GetString()!
                    : throw FieldError("stockArchives entries must be path strings.", "stockArchives"));
        }
        var extra = new List<ResourceReadRequest>();
        if (root.TryGetProperty("extraResources", out var extraArray))
        {
            if (extraArray.ValueKind != JsonValueKind.Array || extraArray.GetArrayLength() > 256)
                throw FieldError("extraResources must be an array of at most 256 resources.", "extraResources");
            foreach (var item in extraArray.EnumerateArray())
            {
                Fields(item, "name", "folder");
                extra.Add(new(String(item, "name"), String(item, "folder")));
            }
        }
        int OptionalInteger(string field, int fallback) => root.TryGetProperty(field, out _) ? Integer(root, field) : fallback;
        bool acknowledge = false;
        if (root.TryGetProperty("allowUnverifiedReferences", out var allow))
            acknowledge = allow.ValueKind is JsonValueKind.True or JsonValueKind.False ? allow.GetBoolean()
                : throw FieldError("allowUnverifiedReferences must be a boolean.", "allowUnverifiedReferences");
        long maximumBytes = LevelPackageOptions.HardMaxPackageBytes;
        if (root.TryGetProperty("maxPackageBytes", out var maximum))
        {
            if (maximum.ValueKind != JsonValueKind.Number || !maximum.TryGetInt64(out maximumBytes))
                throw FieldError("maxPackageBytes must be an integer.", "maxPackageBytes");
        }
        var options = new LevelPackageOptions(String(root, "levelFileName"),
            root.TryGetProperty("episodeTitle", out _) ? String(root, "episodeTitle") : "SED package",
            OptionalInteger("episodeType", 1), stock, extra, acknowledge,
            OptionalInteger("maxNodes", 4096), OptionalInteger("maxEdges", 16384), OptionalInteger("maxDepth", 32), maximumBytes);
        // Validate all declarations, especially stock membership, before opening an archive.
        options = options.Capture(archives.Select(a => new ResourceArchiveReceipt(a.Path, a.Sha256, 0)).ToArray());
        path = Path.GetFullPath(path);
        byte[] source = ReadBoundedFile(path);
        if (Hash(source) != expectedInput)
            throw new AgentConflictException("The source JKL differs from inputSha256. Inspect it and request a fresh plan.");
        using var resources = archives.Count == 0 ? null : new ExplicitArchiveResources(archives);
        var prepared = LevelPackagePlanner.Prepare(source, resources, options);
        // A generous CLI bound; MCP applies its narrower response bound before journaling.
        if (JsonSerializer.SerializeToUtf8Bytes(prepared.Report, JsonOptions).Length + 1 > 8 * 1024 * 1024)
            throw new AgentRequestException("Package manifest exceeds the 8 MiB report budget. Use smaller graph limits or a smaller package.");
        EnsureInputsUnchanged(path, expectedInput, []);
        resources?.VerifyUnchanged();
        return new(path, prepared, archives);
    }

    public static object CommitPackage(PreparedAgentPackage prepared, string outputPath,
        string expectedOutputSha256, string expectedManifestSha256, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!prepared.CanBuild || prepared.Package.OutputBytes is not { } bytes)
            throw new AgentValidationException("The package plan is blocked. Resolve its errors or review its unverified scopes.", prepared.Report);
        string expectedOutput = ResourceManifest.NormalizeHash(expectedOutputSha256, "expectedOutputSha256");
        string expectedManifest = ResourceManifest.NormalizeHash(expectedManifestSha256, "expectedManifestSha256");
        if (prepared.OutputSha256 != expectedOutput || prepared.ManifestSha256 != expectedManifest)
            throw new AgentConflictException("The package output or manifest differs from the reviewed plan. Plan again.");
        outputPath = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(outputPath).Equals(prepared.ArchiveExtension, StringComparison.OrdinalIgnoreCase))
            throw new AgentRequestException($"This package requires the {prepared.ArchiveExtension} output extension.");
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
            throw new AgentConflictException("Output already exists; choose a new package file.");
        string parent = Path.GetDirectoryName(outputPath)!;
        if (!Directory.Exists(parent)) throw new AgentRequestException("Output directory does not exist.");
        EnsureInputsUnchanged(prepared.InputPath, prepared.InputSha256, []);
        using var resources = prepared.Archives.Count == 0 ? null : new ExplicitArchiveResources(prepared.Archives);
        token.ThrowIfCancellationRequested();
        string temporary = Path.Combine(parent, ".sed-package-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            EnsureInputsUnchanged(prepared.InputPath, prepared.InputSha256, []);
            resources?.VerifyUnchanged();
            token.ThrowIfCancellationRequested();
            AtomicFilePromotion.MoveNew(temporary, outputPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { schemaVersion = SchemaVersion, written = outputPath, plan = prepared.Report };
    }
}

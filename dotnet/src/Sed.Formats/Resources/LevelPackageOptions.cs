using System.Text.Json;
using Sed.Formats.Automation;
using Sed.Formats.Launching;

namespace Sed.Formats.Resources;

public sealed record LevelPackageOptions(string LevelFileName, string EpisodeTitle = "SED package",
    int EpisodeType = 1, IReadOnlyList<string>? StockArchives = null,
    IReadOnlyList<ResourceReadRequest>? ExtraResources = null, bool AllowUnverifiedReferences = false,
    int MaxNodes = 4096, int MaxEdges = 16384, int MaxDepth = 32,
    long MaxPackageBytes = 134217728)
{
    public const long HardMaxPackageBytes = 134217728;

    internal LevelPackageOptions Capture(IReadOnlyList<ResourceArchiveReceipt> archives)
    {
        try { TestEpisode.ValidateLevelFileName(LevelFileName); }
        catch (ArgumentException error) { throw new AgentRequestException(error.Message); }
        if (EpisodeTitle is null || EpisodeTitle.Length is < 1 or > 80 ||
            EpisodeTitle.Any(c => c < ' ' || c > '~' || c is '"' or '#'))
            throw new AgentRequestException("episodeTitle needs 1..80 printable ASCII characters without quotes or comment markers.");
        if (EpisodeType is not (1 or 2)) throw new AgentRequestException("episodeType must be 1 (single player) or 2 (deathmatch).");
        if (MaxNodes is < 1 or > 4096 || MaxEdges is < 1 or > 16384 || MaxDepth is < 0 or > 32 ||
            MaxPackageBytes is < 1 or > HardMaxPackageBytes)
            throw new AgentRequestException("Package work limits are outside the supported bounds. Use schema package.");
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var stock = new HashSet<string>(comparison);
        if (StockArchives?.Count > 16) throw new AgentRequestException("stockArchives permits at most 16 source archive paths.");
        foreach (var path in StockArchives ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new AgentRequestException("Stock archives must be absolute paths from resourceArchives.");
            string fullPath = Path.GetFullPath(path);
            if (!archives.Any(a => comparison.Equals(a.Path, fullPath)) || !stock.Add(fullPath))
                throw new AgentRequestException("Each stock archive must be a distinct member of resourceArchives.");
        }
        if (ExtraResources?.Count > 256) throw new AgentRequestException("extraResources permits at most 256 explicit roots.");
        var extra = (ExtraResources ?? []).Select(r =>
        {
            if (r is null) throw new AgentRequestException("Each extra resource requires a name and folder.");
            var normalized = PackageResourcePath.Resource(r.Name, r.Folder);
            return new ResourceReadRequest(normalized.Name, normalized.Folder);
        }).ToArray();
        return this with
        {
            LevelFileName = LevelFileName.ToLowerInvariant(),
            StockArchives = Array.AsReadOnly(archives.Where(a => stock.Contains(a.Path)).Select(a => a.Path).ToArray()),
            ExtraResources = Array.AsReadOnly(extra)
        };
    }
}

public sealed record PackageIssue(string Code, string Severity, string Message,
    int? Node = null, int? LineNumber = null, bool RequiresAcknowledgement = false);
public sealed record PackageDependencyEdge(int? Parent, int Node, string Relation,
    int? LineNumber = null, string? SymbolName = null, LevelAssetOrigin? Origin = null);

/// <summary>A captured binary plan. Public reporting exposes identities, never mutable output bytes.</summary>
public sealed class PreparedLevelPackage
{
    public JsonElement Report { get; }
    public string InputSha256 { get; }
    public string? OutputSha256 { get; }
    public string ManifestSha256 { get; }
    public string ArchiveExtension { get; }
    public bool CanBuild { get; }
    internal byte[]? OutputBytes { get; }

    internal PreparedLevelPackage(JsonElement report, byte[]? outputBytes)
    {
        Report = report.Clone();
        InputSha256 = Report.GetProperty("inputSha256").GetString()!;
        OutputSha256 = Report.GetProperty("outputSha256").GetString();
        ManifestSha256 = Report.GetProperty("manifestSha256").GetString()!;
        ArchiveExtension = Report.GetProperty("archiveExtension").GetString()!;
        CanBuild = Report.GetProperty("canBuild").GetBoolean();
        OutputBytes = outputBytes;
    }
}

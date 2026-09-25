using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

public sealed class AgentAssetDiagnosticTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-assets-query-" + Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(_directory, "assets.jkl");
    private string Archive => Path.Combine(_directory, "assets.goo");
    public AgentAssetDiagnosticTests()
    {
        Directory.CreateDirectory(_directory);
        var doc = new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith));
        doc.Materials.AddRange(["original.mat", "absent.mat"]);
        File.WriteAllText(Input, JklWriter.Build(doc));
        // An existence receipt does not assert this synthetic entry is a valid MAT.
        GobWriter.Build(Archive, [("mat/original.mat", new byte[] { 1, 2, 3 })]);
    }
    private string Request(int offset = 0, int maxRows = 10000, string? revision = null)
    {
        var value = new Dictionary<string, object> { ["schemaVersion"] = 1,
            ["inputSha256"] = AgentWorkspace.Load(Input).Hash,
            ["resourceArchives"] = new[] { new ResourceArchiveRequest(Archive, AgentWorkspace.Hash(File.ReadAllBytes(Archive))) },
            ["offset"] = offset, ["limit"] = 1, ["maxRows"] = maxRows };
        if (revision is not null) value["assetResourceRevision"] = revision;
        return JsonSerializer.Serialize(value, AgentWorkspace.JsonOptions);
    }
    private JsonElement Query(string request) => JsonSerializer.SerializeToElement(AgentWorkspace.DiagnoseAssets(Input, request), AgentWorkspace.JsonOptions);

    [Fact]
    public void DirectResourcePagesBindSourceAndReceiptWithoutClaimingClosureOrFileValidity()
    {
        byte[] before = File.ReadAllBytes(Input);
        var first = Query(Request());
        var row = Assert.Single(first.GetProperty("rows").EnumerateArray());
        Assert.Equal("resolved", row.GetProperty("status").GetString());
        Assert.Equal(AgentWorkspace.Hash(new byte[] { 1, 2, 3 }), row.GetProperty("receipt").GetProperty("sha256").GetString());
        Assert.False(first.GetProperty("recursiveClosureVerified").GetBoolean());
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
        Assert.Equal("missing", Query(Request(offset: 1)).GetProperty("rows")[0].GetProperty("status").GetString());
        Assert.Equal(before, File.ReadAllBytes(Input));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void PartialScanAndChangedResourcesRemainExplicit()
    {
        var page = Query(Request(maxRows: 1));
        Assert.False(page.GetProperty("directScanComplete").GetBoolean());
        Assert.NotEmpty(page.GetProperty("diagnostics").EnumerateArray());
        string revision = page.GetProperty("assetResourceRevision").GetString()!;
        GobWriter.Build(Archive, [("mat/original.mat", new byte[] { 4 })]);
        Assert.Throws<AgentConflictException>(() => Query(Request(maxRows: 1, revision: revision)));
        Assert.Throws<AgentRequestException>(() => Query(Request(offset: -1)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

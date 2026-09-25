using System.Text;
using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

public sealed class AgentCogBindingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-cog-agent-" + Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(_directory, "original.jkl");
    private string Archive => Path.Combine(_directory, "scripts.goo");
    private const string Script = "symbols\nsector trigger=0 linkid=3\nthing receiver=-1 nolink\nflex speed=1\nmessage startup\nend\ncode\nstartup:\nreturn;\nend\n";

    public AgentCogBindingTests()
    {
        Directory.CreateDirectory(_directory);
        AgentWorkspace.Create(Input, "mots");
        var doc = AgentWorkspace.Load(Input).Document;
        var cog = new Cog { Name = "original.cog" };
        cog.Values.AddRange(["0", "-1", "1.25"]);
        doc.Level.Cogs.Add(cog);
        File.WriteAllText(Input, JklWriter.Build(doc));
        WriteScript(Script);
    }

    private void WriteScript(string text) => GobWriter.Build(Archive, [("cog/original.cog", Encoding.Latin1.GetBytes(text))]);
    private string Request(bool patch = false, string? scriptHash = null, string? operation = null)
    {
        var request = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1, ["inputSha256"] = AgentWorkspace.Load(Input).Hash,
            ["resourceArchives"] = new[] { new ResourceArchiveRequest(Archive, AgentWorkspace.Hash(File.ReadAllBytes(Archive))) },
        };
        if (patch) request["operations"] = JsonSerializer.Deserialize<JsonElement>(operation ?? $$"""
            [{"op":"rebindCogReference","cog":0,"parameter":0,"symbol":"trigger","kind":"sector","targetIndex":-1,"scriptSha256":"{{scriptHash ?? AgentWorkspace.Hash(Encoding.Latin1.GetBytes(Script))}}"}]
            """);
        else { request["cog"] = 0; request["offset"] = 0; request["limit"] = 2; }
        return JsonSerializer.Serialize(request, AgentWorkspace.JsonOptions);
    }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);

    [Fact]
    public void TypedDiscoveryBindsBytesPagesAndModifiersWithoutWriting()
    {
        byte[] before = File.ReadAllBytes(Input);
        var page = Json(AgentWorkspace.Bindings(Input, Request()));
        Assert.True(page.GetProperty("schemaComplete").GetBoolean());
        Assert.True(page.GetProperty("parameterCountMatches").GetBoolean());
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("nextOffset").GetInt32());
        Assert.Equal(AgentWorkspace.Hash(Encoding.Latin1.GetBytes(Script)), page.GetProperty("scriptSha256").GetString());
        var rows = page.GetProperty("rows");
        Assert.Equal("sector", rows[0].GetProperty("type").GetString());
        Assert.Equal("Level", rows[0].GetProperty("target").GetProperty("status").GetString());
        Assert.Equal(3, rows[0].GetProperty("modifiers").GetProperty("effectiveLinkId").GetInt32());
        Assert.True(rows[0].GetProperty("eventLinkCandidate").GetBoolean());
        Assert.False(rows[1].GetProperty("eventLinkCandidate").GetBoolean());
        Assert.Equal(before, File.ReadAllBytes(Input));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void GuardedPlanCommitReplaysCogResourceAndPreservesOtherValues()
    {
        byte[] before = File.ReadAllBytes(Input);
        var plan = AgentWorkspace.Plan(Input, Request(patch: true));
        string output = Path.Combine(_directory, "rebound.jkl");
        AgentWorkspace.Commit(plan, output, Json(plan.Report).GetProperty("outputSha256").GetString());
        Assert.Equal(new[] { "-1", "-1", "1.25" }, AgentWorkspace.Load(output).Document.Level.Cogs[0].Values);
        Assert.Equal(before, File.ReadAllBytes(Input));
        var effect = Json(plan.Report).GetProperty("changes")[0];
        Assert.Contains("1.25", effect.GetRawText());
    }

    [Fact]
    public void ScriptChangeBeforePlanOrAfterPlanRefusesWithoutOutput()
    {
        var plan = AgentWorkspace.Plan(Input, Request(patch: true));
        WriteScript(Script.Replace("linkid=3", "linkid=5"));
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(Input, Request(patch: true)));
        string output = Path.Combine(_directory, "refused.jkl");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void IncompleteSchemaShowsUnknownRowsAndRefusesTypedMutation()
    {
        string invalid = Script.Replace("flex speed=1", "mystery speed=1");
        WriteScript(invalid);
        var page = Json(AgentWorkspace.Bindings(Input, Request()));
        Assert.False(page.GetProperty("schemaComplete").GetBoolean());
        Assert.Equal("Unknown", page.GetProperty("rows")[0].GetProperty("target").GetProperty("status").GetString());
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(Input,
            Request(patch: true, scriptHash: AgentWorkspace.Hash(Encoding.Latin1.GetBytes(invalid)))));
    }

    [Fact]
    public void MissingScriptKeepsRawValuesAndNeverGuessesTheirTypes()
    {
        GobWriter.Build(Archive, [("cog/other.cog", Encoding.Latin1.GetBytes(Script))]);
        var page = Json(AgentWorkspace.Bindings(Input, Request()));
        Assert.Equal("missing", page.GetProperty("scriptStatus").GetString());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("scriptSha256").ValueKind);
        Assert.Equal("0", page.GetProperty("rows")[0].GetProperty("rawValue").GetString());
        Assert.Equal("unknown", page.GetProperty("rows")[0].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("\"cog\":0", "\"cog\":-1")]
    [InlineData("\"offset\":0", "\"offset\":-1")]
    [InlineData("\"limit\":2", "\"limit\":201")]
    public void InvalidPageInputsRefuse(string original, string replacement)
    {
        string compact = JsonSerializer.Serialize(JsonDocument.Parse(Request()).RootElement);
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Bindings(Input, compact.Replace(original, replacement)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

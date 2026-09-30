using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Mcp.Infrastructure;

namespace Sed.Mcp.Tests;

/// <summary>Dispatcher tests use original untextured rooms and synthetic archives, never retail/community assets.</summary>
public sealed class SedToolsTests
{
    [Fact]
    public async Task DynamicVertexColorsUsesSharedReadOnlyDiagnosticWithBoundedSourceBoundPages()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        byte[] original = File.ReadAllBytes(input);
        using var tools = fixture.Tools();
        var schema = Success(await tools.CallAsync("sed_discover", Args(new { topic = "vertexColors" })));
        Assert.Contains("schemaVersion", schema.GetRawText());
        var page = Success(await tools.CallAsync("sed_diagnose", Args(new { level = input, kind = "vertexColors",
            request = new { schemaVersion = 1, offset = 1, limit = 2 } })));
        Assert.Equal("assessed", page.GetProperty("status").GetString());
        Assert.Equal(AgentWorkspace.Hash(original), page.GetProperty("sourceRevision").GetString());
        Assert.Equal(2, page.GetProperty("returnedCount").GetInt32());
        Assert.All(page.GetProperty("items").EnumerateArray(), row => {
            Assert.Equal("Compacted", row.GetProperty("retail").GetString());
            Assert.Equal("Retained", row.GetProperty("openMots").GetString());
        });
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Single(Directory.GetFiles(fixture.Root, "*.jkl"));
        Assert.False(File.Exists(fixture.Options.AgentPath));
    }

    [Fact]
    public async Task WorkbenchOperationsUseExistingMcpPlanApplyQueryAndOutcome()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        var doc = AgentWorkspace.Load(input).Document;
        var addresses = doc.Level.Sectors[0].Surfaces.Select((s, i) => (s, i))
            .Where(x => System.Math.Abs(x.s.Normal.Z) > .9).Select(x => new { sector = 0, surface = x.i }).ToArray();
        Assert.Equal(2, addresses.Length);
        using var tools = fixture.Tools();
        var schema = Success(await tools.CallAsync("sed_discover", Args(new { topic = "projectSurfaceUv" })));
        Assert.Contains("texelsPerUnit", schema.GetRawText());
        var request = new {
            schemaVersion = 1, inputSha256 = AgentWorkspace.Hash(File.ReadAllBytes(input)),
            operations = new object[] {
                new { op = "projectSurfaceUv", surfaces = addresses, direction = "+z", origin = new[] { 0, 0, 0 }, texelsPerUnit = 64 },
                new { op = "addLightArray", rgb = new[] { .25, .5, .75 }, intensity = 1, rgbIntensity = 0,
                    range = 2, flags = 5, layout = new { kind = "linear", origin = new[] { 0, 0, 0 }, step = new[] { .1, 0, 0 }, count = 3 } }
            }
        };
        byte[] original = File.ReadAllBytes(input);
        var plan = Success(await tools.CallAsync("sed_plan", Args(new { level = input, request })));
        string operationId = Guid.NewGuid().ToString();
        var committed = Success(await tools.CallAsync("sed_apply", Args(new { level = input, request,
            output = "workbench.jkl", expectedOutputSha256 = plan.GetProperty("outputSha256").GetString(), operationId })));
        Assert.Equal("committed", committed.GetProperty("outcome").GetProperty("observedState").GetString());
        var page = Success(await tools.CallAsync("sed_query", Args(new { level = "workbench.jkl",
            request = new { schemaVersion = 1, collection = "lights", filter = new { flags = new { all = 5, none = 2 } } } })));
        Assert.Equal(3, page.GetProperty("items").GetArrayLength());
        var reopened = AgentWorkspace.Load(fixture.PathFor("workbench.jkl")).Document;
        Assert.All(reopened.Level.Lights, light => Assert.Equal(0, light.RgbIntensity));
        Assert.Equal(original, File.ReadAllBytes(input));
        var outcome = Success(await tools.CallAsync("sed_outcome", Args(new { operationId })));
        Assert.Contains("committed", outcome.GetRawText());
    }

    [Fact]
    public async Task DiscoveryReturnsCompactCapabilitiesAndFocusedSchemasWithoutWorkerLaunch()
    {
        using var fixture = new Fixture();
        using var tools = fixture.Tools();
        var discovered = Success(await tools.CallAsync("sed_discover", null));
        Assert.Equal("sed-mcp", discovered.GetProperty("adapter").GetString());
        Assert.Contains("sed_snapshot", discovered.GetProperty("tools").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(128, discovered.GetProperty("limits").GetProperty("patchOperations").GetInt32());
        Assert.Equal(0, discovered.GetProperty("limits").GetProperty("queuedOperations").GetInt32());
        var schema = Success(await tools.CallAsync("sed_discover", Args(new { topic = "setGravity" })));
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.jkl"));
        Assert.False(File.Exists(fixture.Options.AgentPath));
    }

    [Fact]
    public async Task SharedCogAndFrameAuthoringUseOrdinaryReviewedMcpCommit()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        var document = AgentWorkspace.Load(input).Document;
        var mover = new Template { Name = "original_mover", Parent = "none" };
        mover.Values["move"] = "path"; mover.Values["type"] = "cog";
        document.Level.Templates.Add(mover.Name, mover);
        var thing = document.Level.NewThing(); thing.Name = "gate"; thing.Template = mover.Name;
        thing.Sector = document.Level.Sectors[0];
        File.WriteAllText(input, JklWriter.Build(document));
        const string script = "symbols\nthing gate nolink\nsector exitroom\nmessage startup\nend\ncode\nstartup:\nreturn;\nend\n";
        string archive = fixture.PathFor("original.goo");
        GobWriter.Build(archive, [("cog/original.cog", Encoding.ASCII.GetBytes(script))]);
        using var tools = fixture.Tools();
        var discovered = Success(await tools.CallAsync("sed_discover", Args(new { topic = "addCog" })));
        Assert.Contains("addCog", discovered.GetRawText());
        var request = new Dictionary<string, object> {
            ["schemaVersion"] = 1, ["inputSha256"] = AgentWorkspace.Hash(File.ReadAllBytes(input)),
            ["resourceArchives"] = new[] { new { path = archive, sha256 = AgentWorkspace.Hash(File.ReadAllBytes(archive)) } },
            ["operations"] = new object[] {
                new { op = "setThingFrames", thing = 0, frames = new[] {
                    new { position = new[] { 0.0, 0.0, 0.0 }, orientation = new[] { 0.0, 0.0, 0.0 } },
                    new { position = new[] { 0.0, 0.0, 0.2 }, orientation = new[] { 0.0, 0.0, 0.0 } } } },
                new { op = "addCog", script = "original.cog", scriptSha256 = AgentWorkspace.Hash(Encoding.ASCII.GetBytes(script)),
                    bindings = new[] { new { symbol = "exitroom", kind = "sector", targetIndex = 0 },
                        new { symbol = "gate", kind = "thing", targetIndex = 0 } } } } };
        byte[] original = File.ReadAllBytes(input);
        var plan = Success(await tools.CallAsync("sed_plan", Args(new { level = input, request })));
        request["resourceRevision"] = plan.GetProperty("resourceRevision").GetString()!;
        string id = Guid.NewGuid().ToString();
        var committed = Success(await tools.CallAsync("sed_apply", Args(new { level = input, request,
            output = "playable.jkl", expectedOutputSha256 = plan.GetProperty("outputSha256").GetString(), operationId = id })));
        Assert.Equal("committed", committed.GetProperty("outcome").GetProperty("observedState").GetString());
        Assert.Equal(original, File.ReadAllBytes(input));
        var reopened = AgentWorkspace.Load(fixture.PathFor("playable.jkl")).Document;
        Assert.Equal(new[] { "0", "0" }, Assert.Single(reopened.Level.Cogs).Values);
        Assert.Equal(new[] { "(0/0/0:0/0/0)", "(0/0/0.2:0/0/0)" },
            reopened.Level.Things[0].SourceParameters.Where(p => p.Key == "frame").Select(p => p.Value));
        var bindings = Success(await tools.CallAsync("sed_diagnose", Args(new { level = "playable.jkl", kind = "bindings",
            request = new { schemaVersion = 1, inputSha256 = AgentWorkspace.Hash(File.ReadAllBytes(fixture.PathFor("playable.jkl"))),
                resourceArchives = new[] { new { path = archive, sha256 = AgentWorkspace.Hash(File.ReadAllBytes(archive)) } }, cog = 0 } })));
        Assert.True(bindings.GetProperty("parameterCountMatches").GetBoolean());
    }

    [Theory]
    [InlineData(false, "incompleteExistingCogSchema", "1")]
    [InlineData(true, "existingCogReferenceCapture", "1")]
    [InlineData(true, "existingCogRuntimeReference", "2")]
    public async Task CogAppendClosureRefusalsRemainStructuredAndDoNotWrite(bool includeExistingScript, string diagnostic, string rawReference)
    {
        using var fixture = new Fixture(); string input = fixture.Room();
        var document = AgentWorkspace.Load(input).Document;
        var existing = new Cog {Name="existing.cog"}; existing.Values.Add(rawReference); document.Level.Cogs.Add(existing);
        File.WriteAllText(input,JklWriter.Build(document)); byte[] original=File.ReadAllBytes(input);
        const string script="symbols\nend\n";
        var entries=new List<(string,byte[])> {("cog/new.cog",Encoding.ASCII.GetBytes(script))};
        if (includeExistingScript) entries.Add(("cog/existing.cog",Encoding.ASCII.GetBytes("symbols\ncog future\nend\n")));
        string archive=fixture.PathFor("closure.goo"); GobWriter.Build(archive,entries);
        using var tools=fixture.Tools();
        var result=await tools.CallAsync("sed_plan",Args(new {level=input,request=new {
            schemaVersion=1,inputSha256=AgentWorkspace.Hash(original),
            resourceArchives=new[] {new {path=archive,sha256=AgentWorkspace.Hash(File.ReadAllBytes(archive))}},
            operations=new[] {new {op="addCog",script="new.cog",scriptSha256=AgentWorkspace.Hash(Encoding.ASCII.GetBytes(script)),
                bindings=Array.Empty<object>()}}}}));
        Assert.Equal("invalid_request",Failure(result));
        Assert.Contains(diagnostic,Envelope(result).GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(original,File.ReadAllBytes(input));
        Assert.Single(Directory.GetFiles(fixture.Root,"*.jkl"));
        Assert.Empty(Directory.GetFiles(fixture.Root,".sed-agent-*.tmp"));
    }

    [Theory]
    [InlineData("jk", ProjectType.JediKnight)]
    [InlineData("mots", ProjectType.MysteriesOfTheSith)]
    public async Task NewPlansBeforeWritingAndCommittedHashMatchesReviewedScaffold(string game, ProjectType kind)
    {
        using var fixture = new Fixture();
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_new", Args(new { game })));
        string expected = plan.GetProperty("outputSha256").GetString()!;
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.jkl"));
        string id = Guid.NewGuid().ToString();
        var written = Success(await tools.CallAsync("sed_new", Args(new {
            game, output = "new.jkl", expectedOutputSha256 = expected, operationId = id })));
        string output = fixture.PathFor("new.jkl");
        Assert.Equal(expected, AgentWorkspace.Hash(File.ReadAllBytes(output)));
        Assert.Equal(kind, AgentWorkspace.Load(output).Document.Level.Kind);
        Assert.Equal("committed", written.GetProperty("outcome").GetProperty("observedState").GetString());
        Assert.True(written.GetProperty("outcome").GetProperty("matchesExpectedOutput").GetBoolean());
        Assert.Empty(Directory.GetFiles(fixture.Root, ".sed-agent-*.tmp"));
    }

    [Fact]
    public async Task QueryPlanApplyAndReconnectReplayPreserveInputAndDoNotApplyTwice()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        byte[] original = File.ReadAllBytes(input);
        using var tools = fixture.Tools();
        var query = Success(await tools.CallAsync("sed_query", Args(new {
            level = "room.jkl", request = new { schemaVersion = 1, collection = "summary" } })));
        Assert.Equal(AgentWorkspace.Hash(original), query.GetProperty("inputSha256").GetString());
        var request = fixture.Patch();
        var plan = Success(await tools.CallAsync("sed_plan", Args(new { level = input, request })));
        string hash = plan.GetProperty("outputSha256").GetString()!;
        Assert.False(File.Exists(fixture.PathFor("edited.jkl")));
        Assert.Equal(original, File.ReadAllBytes(input));
        string id = Guid.NewGuid().ToString();
        var arguments = Args(new { level = input, request, output = "edited.jkl", expectedOutputSha256 = hash, operationId = id });
        var committed = Success(await tools.CallAsync("sed_apply", arguments));
        string output = fixture.PathFor("edited.jkl");
        byte[] created = File.ReadAllBytes(output);
        Assert.Equal(hash, AgentWorkspace.Hash(created));
        Assert.Equal(6, AgentWorkspace.Load(output).Document.Level.Header.Gravity);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal("committed", committed.GetProperty("outcome").GetProperty("observedState").GetString());

        // Original source later disappearing must not cause an already recorded write to be replayed.
        File.Delete(input);
        using var reconnected = fixture.Tools();
        var replay = Success(await reconnected.CallAsync("sed_apply", arguments));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(created, File.ReadAllBytes(output));
        var outcome = Success(await reconnected.CallAsync("sed_outcome", Args(new { operationId = id })));
        Assert.Equal("committed", outcome.GetProperty("outcome").GetProperty("observedState").GetString());
        Assert.True(outcome.GetProperty("outcome").GetProperty("matchesExpectedOutput").GetBoolean());
    }

    [Fact]
    public async Task ReorderedJsonPropertiesHaveSameReplayIdentityButChangedArgumentsConflict()
    {
        using var fixture = new Fixture();
        fixture.Room();
        using var tools = fixture.Tools();
        var request = fixture.Patch();
        string hash = Success(await tools.CallAsync("sed_plan", Args(new { level = "room.jkl", request })))
            .GetProperty("outputSha256").GetString()!;
        string id = Guid.NewGuid().ToString();
        var original = Args(new { level = "room.jkl", request, output = "edited.jkl", expectedOutputSha256 = hash, operationId = id });
        Success(await tools.CallAsync("sed_apply", original));
        var reordered = Args(new { operationId = id, expectedOutputSha256 = hash, output = "edited.jkl", request, level = "room.jkl" });
        Assert.True(Success(await tools.CallAsync("sed_apply", reordered)).GetProperty("replayed").GetBoolean());
        reordered["output"] = Element("another.jkl");
        Assert.Equal("conflict", Failure(await tools.CallAsync("sed_apply", reordered)));
        Assert.False(File.Exists(fixture.PathFor("another.jkl")));
    }

    [Fact]
    public async Task StaleReviewedOutputHashRefusesWriteWithoutCreatingOperation()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        byte[] before = File.ReadAllBytes(input);
        using var tools = fixture.Tools();
        string id = Guid.NewGuid().ToString();
        Assert.Equal("conflict", Failure(await tools.CallAsync("sed_apply", Args(new {
            level = input, request = fixture.Patch(), output = "refused.jkl", expectedOutputSha256 = new string('0', 64), operationId = id }))));
        Assert.False(File.Exists(fixture.PathFor("refused.jkl")));
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(JsonValueKind.Null, Success(await tools.CallAsync("sed_outcome", Args(new { operationId = id }))).GetProperty("outcome").ValueKind);
    }

    [Theory]
    [InlineData("inputSha256")]
    [InlineData("sourceRevision")]
    public async Task ChangedSourceIdentityRefusesBeforeWriting(string identity)
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        using var tools = fixture.Tools();
        var request = fixture.Patch();
        string hash = Success(await tools.CallAsync("sed_plan", Args(new { level = input, request })))
            .GetProperty("outputSha256").GetString()!;
        request[identity] = new string('0', 64);
        Assert.Equal("conflict", Failure(await tools.CallAsync("sed_apply", Args(new {
            level = input, request, output = "refused.jkl", expectedOutputSha256 = hash, operationId = Guid.NewGuid().ToString() }))));
        Assert.False(File.Exists(fixture.PathFor("refused.jkl")));
    }

    [Fact]
    public async Task ResourceConsumingCommitRequiresExactPlanResourceRevision()
    {
        using var fixture = new Fixture();
        fixture.Room();
        string archive = fixture.Archive("before");
        using var tools = fixture.Tools();
        var request = fixture.Patch(archive);
        var plan = Success(await tools.CallAsync("sed_plan", Args(new { level = "room.jkl", request })));
        string expected = plan.GetProperty("outputSha256").GetString()!;
        string revision = plan.GetProperty("resourceRevision").GetString()!;
        Assert.Equal(64, revision.Length);
        Assert.Equal("conflict", Failure(await tools.CallAsync("sed_apply", Args(new {
            level = "room.jkl", request, output = "missing-revision.jkl", expectedOutputSha256 = expected, operationId = Guid.NewGuid().ToString() }))));
        request["resourceRevision"] = revision;
        Success(await tools.CallAsync("sed_apply", Args(new {
            level = "room.jkl", request, output = "reviewed.jkl", expectedOutputSha256 = expected, operationId = Guid.NewGuid().ToString() })));
        Assert.Equal(expected, AgentWorkspace.Hash(File.ReadAllBytes(fixture.PathFor("reviewed.jkl"))));
        Assert.False(File.Exists(fixture.PathFor("missing-revision.jkl")));
    }

    [Fact]
    public async Task ChangedArchiveCannotReuseHashOrReviewedResourceRevisionEvenWhenOutputIsSame()
    {
        using var fixture = new Fixture();
        fixture.Room();
        string archive = fixture.Archive("before");
        using var tools = fixture.Tools();
        var request = fixture.Patch(archive);
        var originalPlan = Success(await tools.CallAsync("sed_plan", Args(new { level = "room.jkl", request })));
        request["resourceRevision"] = originalPlan.GetProperty("resourceRevision").GetString()!;
        fixture.Archive("after");
        var commit = Args(new { level = "room.jkl", request, output = "refused.jkl",
            expectedOutputSha256 = originalPlan.GetProperty("outputSha256").GetString(), operationId = Guid.NewGuid().ToString() });
        Assert.Equal("conflict", Failure(await tools.CallAsync("sed_apply", commit)));
        var refreshed = fixture.Patch(archive);
        var currentPlan = Success(await tools.CallAsync("sed_plan", Args(new { level = "room.jkl", request = refreshed })));
        Assert.Equal(originalPlan.GetProperty("outputSha256").GetString(), currentPlan.GetProperty("outputSha256").GetString());
        Assert.NotEqual(originalPlan.GetProperty("resourceRevision").GetString(), currentPlan.GetProperty("resourceRevision").GetString());
        refreshed["resourceRevision"] = originalPlan.GetProperty("resourceRevision").GetString()!;
        commit["request"] = Element(refreshed);
        Assert.Equal("conflict", Failure(await tools.CallAsync("sed_apply", commit)));
        Assert.False(File.Exists(fixture.PathFor("refused.jkl")));
    }

    [Fact]
    public async Task ConvertPlanAndCommitUseSameReviewedHashAndPreserveSource()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        byte[] before = File.ReadAllBytes(input);
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_convert", Args(new { level = input })));
        string hash = plan.GetProperty("outputSha256").GetString()!;
        Success(await tools.CallAsync("sed_convert", Args(new { level = input, output = "converted.jkl",
            operationId = Guid.NewGuid().ToString(), expectedOutputSha256 = hash,
            expectedInputSha256 = plan.GetProperty("inputSha256").GetString(),
            expectedSourceRevision = plan.GetProperty("sourceRevision").GetString() })));
        Assert.Equal(hash, AgentWorkspace.Hash(File.ReadAllBytes(fixture.PathFor("converted.jkl"))));
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("expectedInputSha256")]
    [InlineData("expectedSourceRevision")]
    [InlineData("both")]
    public async Task ConversionCommitRequiresBothReviewedSourceGuards(string missing)
    {
        using var fixture = new Fixture(); string input = fixture.Room(); byte[] before = File.ReadAllBytes(input);
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_convert", Args(new { level = input })));
        string id = Guid.NewGuid().ToString();
        var commit = Args(new { level = input, output = "refused.jkl", operationId = id,
            expectedOutputSha256 = plan.GetProperty("outputSha256").GetString(),
            expectedInputSha256 = plan.GetProperty("inputSha256").GetString(),
            expectedSourceRevision = plan.GetProperty("sourceRevision").GetString() });
        if (missing == "both") { commit.Remove("expectedInputSha256"); commit.Remove("expectedSourceRevision"); }
        else commit.Remove(missing);
        Assert.Equal("invalid_request", Failure(await tools.CallAsync("sed_convert", commit)));
        Assert.False(File.Exists(fixture.PathFor("refused.jkl")));
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Equal(JsonValueKind.Null, Success(await tools.CallAsync("sed_outcome", Args(new { operationId = id }))).GetProperty("outcome").ValueKind);
    }

    [Theory]
    [InlineData("expectedInputSha256")]
    [InlineData("expectedSourceRevision")]
    public async Task ConversionRefusesStaleSourceEvenWhenLineEndingNormalizationLeavesOutputUnchanged(string staleGuard)
    {
        using var fixture = new Fixture(); string input = fixture.Room();
        using var tools = fixture.Tools();
        string lf = File.ReadAllText(input).Replace("\r\n", "\n");
        Assert.Contains('\n', lf);
        File.WriteAllText(input, lf, new UTF8Encoding(false));
        var reviewed = Success(await tools.CallAsync("sed_convert", Args(new { level = input })));
        File.WriteAllText(input, lf.Replace("\n", "\r\n"), new UTF8Encoding(false));
        byte[] changedSource = File.ReadAllBytes(input);
        var current = Success(await tools.CallAsync("sed_convert", Args(new { level = input })));
        Assert.NotEqual(reviewed.GetProperty("inputSha256").GetString(), current.GetProperty("inputSha256").GetString());
        Assert.NotEqual(reviewed.GetProperty("sourceRevision").GetString(), current.GetProperty("sourceRevision").GetString());
        Assert.Equal(reviewed.GetProperty("outputSha256").GetString(), current.GetProperty("outputSha256").GetString());
        string id = Guid.NewGuid().ToString();
        var commit = Args(new { level = input, output = "refused.jkl", operationId = id,
            expectedOutputSha256 = reviewed.GetProperty("outputSha256").GetString(),
            expectedInputSha256 = current.GetProperty("inputSha256").GetString(),
            expectedSourceRevision = current.GetProperty("sourceRevision").GetString() });
        commit[staleGuard] = reviewed.GetProperty(staleGuard == "expectedInputSha256" ? "inputSha256" : "sourceRevision");
        Assert.Equal("conflict", Failure(await tools.CallAsync("sed_convert", commit)));
        Assert.False(File.Exists(fixture.PathFor("refused.jkl")));
        Assert.Equal(changedSource, File.ReadAllBytes(input));
        Assert.Equal(JsonValueKind.Null, Success(await tools.CallAsync("sed_outcome", Args(new { operationId = id }))).GetProperty("outcome").ValueKind);
    }

    [Fact]
    public async Task NativeConversionRefusesChangedTemplateRevisionEvenWhenInputAndOutputBytesMatch()
    {
        using var fixture = new Fixture(); string input = fixture.PathFor("native.sed");
        File.WriteAllText(input, "SED 0.3.0\nSEC: MOTS\nEND\nSEC: HEADER\nHDR 1 4 15 2 3 - 320\nHOR 100 768 0 0\nCEIL 0 0\nMIP 1 2 3 4\nLOD 0.1 0.2 0.3 0.4\nEND\n");
        string template = fixture.PathFor("mots.tpl");
        File.WriteAllText(template, "mcpconversionprobe none type=ghost\n");
        byte[] source = File.ReadAllBytes(input);
        using var tools = fixture.Tools();
        var reviewed = Success(await tools.CallAsync("sed_convert", Args(new { level = input })));
        File.AppendAllText(template, "# template library was revised after planning\n");
        byte[] revisedTemplate = File.ReadAllBytes(template);
        var current = Success(await tools.CallAsync("sed_convert", Args(new { level = input })));
        Assert.Equal(reviewed.GetProperty("inputSha256").GetString(), current.GetProperty("inputSha256").GetString());
        Assert.Equal(reviewed.GetProperty("outputSha256").GetString(), current.GetProperty("outputSha256").GetString());
        Assert.NotEqual(reviewed.GetProperty("sourceRevision").GetString(), current.GetProperty("sourceRevision").GetString());
        string id = Guid.NewGuid().ToString();
        Assert.Equal("conflict", Failure(await tools.CallAsync("sed_convert", Args(new {
            level = input, output = "refused.jkl", operationId = id,
            expectedOutputSha256 = reviewed.GetProperty("outputSha256").GetString(),
            expectedInputSha256 = reviewed.GetProperty("inputSha256").GetString(),
            expectedSourceRevision = reviewed.GetProperty("sourceRevision").GetString() }))));
        Assert.False(File.Exists(fixture.PathFor("refused.jkl")));
        Assert.Equal(source, File.ReadAllBytes(input));
        Assert.Equal(revisedTemplate, File.ReadAllBytes(template));
        Assert.Equal(JsonValueKind.Null, Success(await tools.CallAsync("sed_outcome", Args(new { operationId = id }))).GetProperty("outcome").ValueKind);
    }

    [Theory]
    [InlineData("sed_missing", "{}")]
    [InlineData("sed_discover", "{\"topic\":\"query\",\"extra\":1}")]
    [InlineData("sed_discover", "{\"topic\":7}")]
    [InlineData("sed_discover", "{\"topic\":\"   \"}")]
    [InlineData("sed_query", "{\"level\":\"room.jkl\"}")]
    [InlineData("sed_query", "{\"level\":\"room.jkl\",\"request\":[]}")]
    [InlineData("sed_query", "{\"level\":17,\"request\":{}}")]
    [InlineData("sed_query", "{\"level\":\"room.jkl\",\"request\":{\"schemaVersion\":1,\"schemaVersion\":2,\"collection\":\"summary\"}}")]
    [InlineData("sed_query", "{\"level\":\"room.jkl\",\"request\":{\"schemaVersion\":99,\"collection\":\"summary\"}}")]
    [InlineData("sed_query", "{\"level\":\"room.jkl\",\"request\":{\"schemaVersion\":1,\"collection\":\"summary\",\"extra\":1}}")]
    [InlineData("sed_new", "{\"game\":\"unsupported\"}")]
    [InlineData("sed_new", "{\"game\":\"mots\",\"output\":\"new.jkl\"}")]
    [InlineData("sed_new", "{\"game\":\"mots\",\"operationId\":\"not-a-guid\"}")]
    [InlineData("sed_convert", "{\"level\":\"room.jkl\",\"expectedOutputSha256\":\"bad\"}")]
    [InlineData("sed_diagnose", "{\"level\":\"room.jkl\",\"kind\":\"unsupported\"}")]
    [InlineData("sed_outcome", "{\"operationId\":\"not-a-guid\"}")]
    public async Task InvalidArgumentsReturnStructuredErrorsWithoutChangingInput(string name, string json)
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        byte[] before = File.ReadAllBytes(input);
        using var tools = fixture.Tools();
        Assert.Equal("invalid_request", Failure(await tools.CallAsync(name, Args(json))));
        Assert.Equal(before, File.ReadAllBytes(input));
        Assert.Single(Directory.GetFiles(fixture.Root, "*.jkl"));
    }

    [Fact]
    public async Task PatchLimitIsEnforcedBeforeDomainEvaluation()
    {
        using var fixture = new Fixture();
        fixture.Room();
        using var tools = fixture.Tools();
        var request = fixture.Patch();
        request["operations"] = Enumerable.Range(0, SedTools.MaxPatchOperations + 1)
            .Select(i => new { op = "setGravity", value = i }).ToArray();
        var error = await tools.CallAsync("sed_plan", Args(new { level = "room.jkl", request }));
        Assert.Equal("invalid_request", Failure(error));
        Assert.Contains("128", Envelope(error).GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ToolByteBudgetAndLevelFileBudgetAreEnforced()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        using var tools = fixture.Tools();
        var tooLarge = new string('x', AgentWorkspace.MaxRequestBytes + 65537);
        Assert.Equal("invalid_request", Failure(await tools.CallAsync("sed_query", Args(new {
            level = input, request = new { schemaVersion = 1, collection = "summary", padding = tooLarge } }))));
        using (var file = new FileStream(input, FileMode.Open, FileAccess.Write)) file.SetLength(SedTools.MaxInputBytes + 1);
        var error = await tools.CallAsync("sed_query", Args(new { level = input, request = new { schemaVersion = 1, collection = "summary" } }));
        Assert.Equal("invalid_request", Failure(error));
        Assert.Contains("limit", Envelope(error).GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ExternalReadAndWritePathsAreDeniedWithoutInspectingTheirContents()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        using var external = new Fixture();
        string outside = external.Room();
        byte[] before = File.ReadAllBytes(outside);
        using var tools = fixture.Tools();
        PolicyDenied(await tools.CallAsync("sed_query", Args(new {
            level = outside, request = new { schemaVersion = 1, collection = "summary" } })), "Read path is outside");
        var request = fixture.Patch();
        string hash = Success(await tools.CallAsync("sed_plan", Args(new { level = input, request })))
            .GetProperty("outputSha256").GetString()!;
        PolicyDenied(await tools.CallAsync("sed_apply", Args(new {
            level = input, request, output = external.PathFor("refused.jkl"), expectedOutputSha256 = hash, operationId = Guid.NewGuid().ToString() })), "Outputs must be files inside");
        Assert.Equal(before, File.ReadAllBytes(outside));
        Assert.False(File.Exists(external.PathFor("refused.jkl")));
    }

    [Fact]
    public async Task AuthorizedReadRootAllowsSourceButNeverExternalOutput()
    {
        using var fixture = new Fixture();
        using var external = new Fixture();
        string source = external.Room();
        using var tools = new SedTools(fixture.Options with { ResourceRoots = [external.Root] });
        Success(await tools.CallAsync("sed_query", Args(new { level = source, request = new { schemaVersion = 1, collection = "summary" } })));
        var request = external.Patch();
        string hash = Success(await tools.CallAsync("sed_plan", Args(new { level = source, request })))
            .GetProperty("outputSha256").GetString()!;
        PolicyDenied(await tools.CallAsync("sed_apply", Args(new {
            level = source, request, output = external.PathFor("refused.jkl"), expectedOutputSha256 = hash, operationId = Guid.NewGuid().ToString() })), "Outputs must be files inside");
        Success(await tools.CallAsync("sed_apply", Args(new {
            level = source, request, output = "accepted.jkl", expectedOutputSha256 = hash, operationId = Guid.NewGuid().ToString() })));
        Assert.True(File.Exists(fixture.PathFor("accepted.jkl")));
    }

    [Fact]
    public async Task ResourceArchiveCannotEscapeReadRoots()
    {
        using var fixture = new Fixture();
        using var external = new Fixture();
        fixture.Room();
        string archive = external.Archive("private synthetic fixture");
        using var tools = fixture.Tools();
        PolicyDenied(await tools.CallAsync("sed_plan", Args(new { level = "room.jkl", request = fixture.Patch(archive) })), "Read path is outside");
        PolicyDenied(await tools.CallAsync("sed_archive", Args(new { archive })), "Read path is outside");
    }

    [Theory]
    [InlineData("jed")]
    [InlineData("sed")]
    [InlineData("ned")]
    public async Task NativeSnapshotRequiresReviewedJklConversionBeforeWorkerLaunch(string extension)
    {
        using var fixture = new Fixture();
        string native = fixture.PathFor("original." + extension);
        File.WriteAllText(native, "SED 0.3.0\nSEC: MOTS\nEND\nSEC: HEADER\nHDR 1 4 15 2 3 - 320\nHOR 100 768 0 0\nCEIL 0 0\nMIP 1 2 3 4\nLOD 0.1 0.2 0.3 0.4\nEND\n");
        byte[] before = File.ReadAllBytes(native);
        using var tools = fixture.Tools();
        var error = await tools.CallAsync("sed_snapshot", Args(new {
            level = native, output = "refused.png", request = new {
                schemaVersion = 1, inputSha256 = AgentWorkspace.Hash(before),
                position = new[] { 0.0, -0.8, 0.0 }, target = new[] { 0.0, 1.0, 0.0 }, width = 32, height = 32 } }));
        Assert.Equal("invalid_request", Failure(error));
        Assert.StartsWith("MCP snapshots require JKL.", Envelope(error).GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(before, File.ReadAllBytes(native));
        Assert.False(File.Exists(fixture.Options.AgentPath));
        Assert.False(File.Exists(fixture.PathFor("refused.png")));
    }

    [Fact]
    public async Task CancellationBeforeDispatchCreatesNoFileOrOperation()
    {
        using var fixture = new Fixture();
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_new", Args(new { game = "mots" })));
        string id = Guid.NewGuid().ToString();
        Assert.Equal("cancelled", Failure(await tools.CallAsync("sed_new", Args(new {
            game = "mots", output = "cancelled.jkl", operationId = id,
            expectedOutputSha256 = plan.GetProperty("outputSha256").GetString() }), new CancellationToken(true))));
        Assert.False(File.Exists(fixture.PathFor("cancelled.jkl")));
        Assert.Equal(JsonValueKind.Null, Success(await tools.CallAsync("sed_outcome", Args(new { operationId = id }))).GetProperty("outcome").ValueKind);
    }

    [Fact]
    public async Task CancellationBeforeSnapshotDispatchUsesPreviewRecoveryWithoutCreatingOutputOrReceipt()
    {
        using var fixture = new Fixture();
        fixture.Room();
        using var tools = fixture.Tools();
        var result = await tools.CallAsync("sed_snapshot", Args(new {
            level = "room.jkl", output = "cancelled.png", request = new {
                schemaVersion = 1, inputSha256 = AgentWorkspace.Hash(File.ReadAllBytes(fixture.PathFor("room.jkl"))),
                position = new[] { 0.0, -0.8, 0.0 }, target = new[] { 0.0, 1.0, 0.0 }, width = 32, height = 32 }
        }), new CancellationToken(true));

        Assert.Equal("cancelled", Failure(result));
        string message = Envelope(result).GetProperty("error").GetProperty("message").GetString()!;
        Assert.Contains("no durable outcome record", message);
        Assert.Contains("Inspect the intended PNG path with authorized file/image tools", message);
        Assert.Contains("never retry blindly", message);
        Assert.DoesNotContain("sed_outcome", message);
        Assert.DoesNotContain("operationId", message);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(Envelope(result).GetRawText()) < 1024);
        Assert.False(File.Exists(fixture.PathFor("cancelled.png")));
        Assert.Empty(Directory.EnumerateFiles(fixture.PathFor(".sed-mcp/receipts")));
        Assert.False(File.Exists(fixture.Options.AgentPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedSnapshotSuccessPreservesKnownOutputEvidenceWithoutInlineImage(bool unicodeEscaping)
    {
        byte[] png = FormatterPng();
        string hash = AgentWorkspace.Hash(png);
        string path = Path.Combine(Path.GetTempPath(), "sed-mcp-formatter-fixture", "preview.png");
        var state = new SnapshotOutputState(path, "file", false, png.Length, hash);
        var receipt = LargeWorkerReceipt(path, hash, png.Length, unicodeEscaping);
        var worker = new SnapshotWorkerResult(receipt, png, hash, state, new string('e', SnapshotWorker.MaxPipeBytes));

        var result = SedTools.SnapshotSuccess(worker);
        Assert.Equal("snapshot_result_too_large", Failure(result));
        Assert.Empty(result.Content.OfType<ImageContentBlock>());
        var details = Envelope(result).GetProperty("error").GetProperty("details");
        Assert.Equal(hash, details.GetProperty("sha256").GetString());
        Assert.True(details.GetProperty("receiptOmitted").GetBoolean());
        Assert.True(details.GetProperty("standardErrorTruncated").GetBoolean());
        Assert.False(details.TryGetProperty("receipt", out _));
        Assert.True(JsonElement.DeepEquals(Element(state), details.GetProperty("outputState")));
        Assert.Equal(path, details.GetProperty("outputState").GetProperty("path").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(Envelope(result).GetRawText()) <= SedTools.MaxResultBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedSnapshotErrorRetainsOriginalFailureAndOutputStateWhenReceiptIsOmitted(bool unicodeEscaping)
    {
        byte[] png = FormatterPng();
        string hash = AgentWorkspace.Hash(png);
        string path = Path.Combine(Path.GetTempPath(), "sed-mcp-formatter-fixture", "preview.png");
        var state = new SnapshotOutputState(path, "file", false, png.Length, hash,
            "Observed bytes do not establish which process created the file.", ProcessExitConfirmed: false);
        var receipt = LargeWorkerReceipt(path, hash, png.Length, unicodeEscaping, "conflict");
        var workerError = new SnapshotWorkerException("conflict", "Worker's original conflict message", false,
            state, 4, receipt, new string('e', SnapshotWorker.MaxPipeBytes));

        var result = SedTools.SnapshotFailure(workerError);
        Assert.Equal("conflict", Failure(result));
        var error = Envelope(result).GetProperty("error");
        Assert.Equal(workerError.Message, error.GetProperty("message").GetString());
        var details = error.GetProperty("details");
        Assert.True(details.GetProperty("receiptOmitted").GetBoolean());
        Assert.False(details.TryGetProperty("receipt", out _));
        Assert.False(details.GetProperty("isHostFault").GetBoolean());
        Assert.Equal(4, details.GetProperty("exitCode").GetInt32());
        Assert.True(details.GetProperty("standardErrorTruncated").GetBoolean());
        Assert.True(JsonElement.DeepEquals(Element(state), details.GetProperty("outputState")));
        Assert.Equal(hash, details.GetProperty("outputState").GetProperty("sha256").GetString());
        Assert.False(details.GetProperty("outputState").GetProperty("processExitConfirmed").GetBoolean());
        Assert.Empty(result.Content.OfType<ImageContentBlock>());
        Assert.True(Encoding.UTF8.GetByteCount(Envelope(result).GetRawText()) <= SedTools.MaxResultBytes);
    }

    [Fact]
    public void NormalSnapshotReceiptWithMaximumStderrRemainsSuccessfulWithImageAndExplicitTruncation()
    {
        byte[] png = FormatterPng();
        string hash = AgentWorkspace.Hash(png);
        string path = Path.Combine(Path.GetTempPath(), "sed-mcp-formatter-fixture", "preview.png");
        var state = new SnapshotOutputState(path, "file", false, png.Length, hash);
        var receipt = Element(new { schemaVersion = 1, written = path, outputSha256 = hash, outputBytes = png.Length });
        string stderr = new('e', SnapshotWorker.MaxPipeBytes);
        var result = SedTools.SnapshotSuccess(new(receipt, png, hash, state, stderr));
        var data = Success(result);
        Assert.True(JsonElement.DeepEquals(receipt, data.GetProperty("receipt")));
        Assert.True(JsonElement.DeepEquals(Element(state), data.GetProperty("outputState")));
        Assert.Equal(hash, data.GetProperty("sha256").GetString());
        Assert.True(data.GetProperty("standardErrorTruncated").GetBoolean());
        string returnedStderr = data.GetProperty("standardError").GetString()!;
        Assert.NotEmpty(returnedStderr);
        Assert.True(returnedStderr.Length < stderr.Length);
        Assert.StartsWith(returnedStderr, stderr);
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(png, image.DecodedData.ToArray());
        Assert.True(Encoding.UTF8.GetByteCount(Envelope(result).GetRawText()) <= SedTools.MaxResultBytes);
    }

    private static byte[] FormatterPng() =>
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/aX0AAAAASUVORK5CYII=");

    private static JsonElement LargeWorkerReceipt(string path, string hash, int bytes, bool unicodeEscaping, string? error = null)
    {
        var fields = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1, ["written"] = path, ["outputSha256"] = hash, ["outputBytes"] = bytes,
            ["diagnostic"] = "",
        };
        if (error is not null) { fields["error"] = error; fields["message"] = "Original worker error"; }
        var wireOptions = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        int fixedBytes = JsonSerializer.SerializeToUtf8Bytes(fields, wireOptions).Length;
        // Both cases are within the worker's input-to-host pipe bound. One is
        // near that bound already; the other expands only when host JSON escapes
        // literal non-ASCII text. Neither depends on a receipt impossible to receive.
        fields["diagnostic"] = unicodeEscaping
            ? new string('測', SedTools.MaxResultBytes / 6 + 4096)
            : new string('x', SnapshotWorker.MaxPipeBytes - fixedBytes - 16);
        var receipt = JsonSerializer.SerializeToElement(fields, wireOptions);
        Assert.True(Encoding.UTF8.GetByteCount(receipt.GetRawText()) <= SnapshotWorker.MaxPipeBytes);
        return receipt;
    }

    private static JsonElement Envelope(CallToolResult result)
    {
        string text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var document = JsonDocument.Parse(text);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(result.StructuredContent.HasValue);
        Assert.True(JsonElement.DeepEquals(document.RootElement, result.StructuredContent.Value),
            "Text and structured results must contain the same JSON value.");
        return document.RootElement.Clone();
    }
    private static JsonElement Success(CallToolResult result)
    {
        var envelope = Envelope(result);
        Assert.True(envelope.GetProperty("ok").GetBoolean(), envelope.GetRawText());
        Assert.False(result.IsError);
        return envelope.GetProperty("result");
    }
    private static string Failure(CallToolResult result)
    {
        var envelope = Envelope(result);
        Assert.False(envelope.GetProperty("ok").GetBoolean(), envelope.GetRawText());
        Assert.True(result.IsError);
        return envelope.GetProperty("error").GetProperty("code").GetString()!;
    }
    private static void PolicyDenied(CallToolResult result, string detail)
    {
        Assert.Equal("invalid_request", Failure(result));
        Assert.StartsWith("MCP file policy: " + detail, Envelope(result).GetProperty("error").GetProperty("message").GetString());
    }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private static Dictionary<string, JsonElement> Args(object value) => Args(Element(value).GetRawText());
    private static Dictionary<string, JsonElement> Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sed-mcp-dispatch-" + Guid.NewGuid().ToString("N"));
        public HostOptions Options => new(Root, PathFor("missing-fixed-agent.exe"), "synthetic-test-version", []);
        public Fixture() => Directory.CreateDirectory(Root);
        public string PathFor(string file) => Path.Combine(Root, file);
        public SedTools Tools() => new(Options);
        public string Room()
        {
            string input = PathFor("room.jkl");
            File.WriteAllText(input, JklWriter.Build(new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith))));
            return input;
        }
        public string Archive(string content)
        {
            string path = PathFor("synthetic.goo");
            GobWriter.Build(path, [("misc/synthetic.txt", Encoding.UTF8.GetBytes(content))]);
            return path;
        }
        public Dictionary<string, object> Patch(string? archive = null)
        {
            var snapshot = AgentWorkspace.LoadWithProvenance(PathFor("room.jkl"));
            var request = new Dictionary<string, object> {
                ["schemaVersion"] = 1, ["inputSha256"] = snapshot.InputSha256, ["sourceRevision"] = snapshot.SourceRevision,
                ["operations"] = new[] { new { op = "setGravity", value = 6 } } };
            if (archive is not null) request["resourceArchives"] = new[] { new { path = archive, sha256 = AgentWorkspace.Hash(File.ReadAllBytes(archive)) } };
            return request;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

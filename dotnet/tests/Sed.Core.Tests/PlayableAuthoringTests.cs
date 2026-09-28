using System.Text;
using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Cogs;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed partial class PlayableAuthoringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-playable-authoring-" + Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(_directory, "original.jkl");
    private string Archive => Path.Combine(_directory, "original.goo");
    private const string Script = "symbols\nsurface trigger linkid=3\nthing gate nolink\nsector exitsector\ncog master nolink\nflex speed=0.5 local\nmessage activated\nend\ncode\nactivated:\nreturn;\nend\n";

    public PlayableAuthoringTests()
    {
        Directory.CreateDirectory(_directory);
        AgentWorkspace.Create(Input, "mots");
        var doc = AgentWorkspace.Load(Input).Document;
        var template = new Template { Name = "original_mover", Parent = "none", Order = doc.Level.Templates.Count };
        template.Values["type"] = "cog"; template.Values["move"] = "path";
        doc.Level.Templates.Add(template.Name, template);
        var thing = doc.Level.NewThing(); thing.Template = template.Name; thing.Name = "gate";
        thing.Position = Vec3.Zero; thing.Sector = doc.Level.Sectors[0];
        thing.SourceParameters.AddRange([new("numframes", "2"), new("frame", "(0/0/0:0/0/0)"),
            new("health", "100"), new("frame", "(0/0/1:0/0/0)"), new("health", "90")]);
        thing.Values["numframes"] = "2"; thing.Values["frame"] = "(0/0/1:0/0/0)"; thing.Values["health"] = "90";
        doc.Level.Cogs.Add(new Cog { Name = "master.cog" });
        File.WriteAllText(Input, JklWriter.Build(doc));
        WriteScript(Script);
    }

    private void WriteScript(string script, string master = "symbols\nend\n") => GobWriter.Build(Archive,
        [("cog/play01.cog", Encoding.Latin1.GetBytes(script)), ("cog/master.cog", Encoding.Latin1.GetBytes(master))]);
    private static CogScript ExistingLocalScript(string name) => CogScript.Parse(name, "symbols\nend\n");
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private string Patch(object[] operations, bool resources = true)
    {
        var request = new Dictionary<string, object> { ["schemaVersion"] = 1,
            ["inputSha256"] = AgentWorkspace.Load(Input).Hash, ["operations"] = operations };
        if (resources) request["resourceArchives"] = new[] { new { path = Archive, sha256 = AgentWorkspace.Hash(File.ReadAllBytes(Archive)) } };
        return JsonSerializer.Serialize(request);
    }
    private static object AddCog(string? hash = null) => new { op = "addCog", script = "play01.cog",
        scriptSha256 = hash ?? AgentWorkspace.Hash(Encoding.Latin1.GetBytes(Script)), bindings = new[] {
            new { symbol = "master", kind = "cog", targetIndex = 0 },
            new { symbol = "gate", kind = "thing", targetIndex = 0 },
            new { symbol = "exitsector", kind = "sector", targetIndex = 0 },
            new { symbol = "trigger", kind = "surface", targetIndex = 4 } } };
    private static object Frames(double z = 0.25) => new { op = "setThingFrames", thing = 0, frames = new[] {
        new { position = new[] { 0.0, 0.0, 0.0 }, orientation = new[] { 0.0, 0.0, 0.0 } },
        new { position = new[] { 0.0, 0.0, z }, orientation = new[] { 0.0, 90.0, 0.0 } } } };

    [Fact]
    public void CombinedPlanAppendsCogInDeclarationOrderAndPreservesFramesThroughCommit()
    {
        byte[] before = File.ReadAllBytes(Input);
        var plan = AgentWorkspace.Plan(Input, Patch([Frames(), AddCog()]));
        Assert.Equal(before, File.ReadAllBytes(Input));
        var report = Json(plan.Report);
        Assert.Equal(1, report.GetProperty("changes")[1].GetProperty("serializedFinal").GetProperty("target").GetProperty("index").GetInt32());
        string output = Path.Combine(_directory, "authored.jkl");
        AgentWorkspace.Commit(plan, output, report.GetProperty("outputSha256").GetString());
        var reopened = AgentWorkspace.Load(output).Document;
        Assert.Equal(new[] { "4", "0", "0", "0" }, reopened.Level.Cogs[1].Values);
        var path = reopened.Level.Things[0];
        Assert.Equal("2", path.Values["numframes"]);
        Assert.Equal(new[] { "(0/0/0:0/0/0)", "(0/0/0.25:0/90/0)" }, path.SourceParameters.Where(p => p.Key == "frame").Select(p => p.Value));
        Assert.Equal(new[] { "100", "90" }, path.SourceParameters.Where(p => p.Key == "health").Select(p => p.Value));
        Assert.True(path.SourceParameters.FindIndex(p => p.Key == "numframes") < path.SourceParameters.FindIndex(p => p.Key == "frame"));
        var binding = CogBindings.Describe(reopened, 1, CogScript.Parse("play01.cog", Script));
        Assert.True(binding.ParameterCountMatches);
        Assert.Equal(4, binding.Rows[0].Target.SurfaceIndex);
        Assert.False(binding.Rows[1].EventLinkCandidate);
    }

    [Fact]
    public void SharedFrameCommandHasExactUndoAndRejectsNonfiniteOrUnsupportedMovers()
    {
        var doc = AgentWorkspace.Load(Input).Document; var thing = doc.Level.Things[0];
        var records = thing.SourceParameters.ToArray(); var values = thing.Values.ToArray();
        var frames = new[] { new ThingFrame(Vec3.Zero, Vec3.Zero), new ThingFrame(new(0, 0, .5), new(0, 90, 0)) };
        var command = new SetThingFramesCommand(doc.Level, thing, frames);
        Assert.Equal(records, thing.SourceParameters);
        var history = new EditHistory(); history.Do(command);
        Assert.Equal("(0/0/0.5:0/90/0)", thing.Values["frame"]);
        history.Undo(); Assert.Equal(records, thing.SourceParameters); Assert.Equal(values, thing.Values.ToArray());
        history.Redo(); Assert.Equal("(0/0/0.5:0/90/0)", thing.Values["frame"]);
        Assert.Throws<ArgumentException>(() => new SetThingFramesCommand(doc.Level, thing, [new(new(double.NaN, 0, 0), Vec3.Zero)]));
        Assert.Throws<ArgumentException>(() => new SetThingFramesCommand(doc.Level, thing, []));
        Assert.Throws<ArgumentException>(() => new SetThingFramesCommand(doc.Level, thing, Enumerable.Repeat(frames[0], 257).ToArray()));
        thing.ArchLightingIndex = 0;
        Assert.Throws<ArgumentException>(() => new SetThingFramesCommand(doc.Level, thing, frames));
        thing.ArchLightingIndex = -1; thing.Values["move"] = "physics";
        Assert.Throws<ArgumentException>(() => new SetThingFramesCommand(doc.Level, thing, frames));
    }

    [Fact]
    public void NamedBindingPreparationIsUndoableAndRejectsGuesswork()
    {
        var doc = AgentWorkspace.Load(Input).Document;
        var script = CogScript.Parse("one.cog", "symbols\nthing target\nend\n");
        var command = CogBindings.PrepareAdd(doc, "one.cog", script, [new("target", CogSymbolType.Thing, 0)], ExistingLocalScript);
        Assert.Single(doc.Level.Cogs); command.Apply(); Assert.Equal("0", command.Cog.Values[0]);
        command.Revert(); Assert.Single(doc.Level.Cogs);
        foreach (var bindings in new CogNamedReference[][] {
            [], [new("wrong", CogSymbolType.Thing, 0)], [new("target", CogSymbolType.Sector, 0)],
            [new("target", CogSymbolType.Thing, -2)], [new("target", CogSymbolType.Thing, 999)],
            [new("target", CogSymbolType.Thing, 0), new("TARGET", CogSymbolType.Thing, 0)] })
            Assert.Throws<CogBindingException>(() => CogBindings.PrepareAdd(doc, "one.cog", script, bindings));
        var externalScalar = CogScript.Parse("one.cog", "symbols\nflex speed=1\nend\n");
        Assert.Throws<CogBindingException>(() => CogBindings.PrepareAdd(doc, "one.cog", externalScalar, [new("speed", CogSymbolType.Flex, 0)]));
        var externalCog = CogScript.Parse("one.cog", "symbols\ncog target\nend\n");
        foreach (int index in new[] { 1, 32768 })
            Assert.Throws<CogBindingException>(() => CogBindings.PrepareAdd(doc, "one.cog", externalCog, [new("target", CogSymbolType.Cog, index)]));
        Assert.Single(doc.Level.Cogs);
    }

    [Fact]
    public void EmptyBindingsAllowLocalOnlyScriptAndSubsequentOperationCanAddressAppend()
    {
        const string local = "symbols\nint state=0 local\nmessage startup\nend\ncode\nstartup:\nreturn;\nend\n";
        WriteScript(local);
        var add = new { op = "addCog", script = "play01.cog", scriptSha256 = AgentWorkspace.Hash(Encoding.Latin1.GetBytes(local)), bindings = Array.Empty<object>() };
        var plan = AgentWorkspace.Plan(Input, Patch([add, add]));
        Assert.Equal(3, JklParser.ParseDocument(plan.OutputText).Level.Cogs.Count);
    }

    [Fact]
    public void CogAppendThenRebindUsesCurrentPlanState()
    {
        var rebind = new { op = "rebindCogReference", cog = 1, parameter = 3, symbol = "master", kind = "cog", targetIndex = 1,
            scriptSha256 = AgentWorkspace.Hash(Encoding.Latin1.GetBytes(Script)) };
        var plan = AgentWorkspace.Plan(Input, Patch([AddCog(), rebind]));
        Assert.Equal("1", JklParser.ParseDocument(plan.OutputText).Level.Cogs[1].Values[3]);
    }

    [Fact]
    public void LateBatchFailureLeavesOriginalAndOutputUntouched()
    {
        byte[] original = File.ReadAllBytes(Input);
        string output = Path.Combine(_directory, "failed-batch.jkl");
        var invalid = new { op = "rebindCogReference", cog = 1, parameter = 1, symbol = "gate", kind = "thing", targetIndex = 900,
            scriptSha256 = AgentWorkspace.Hash(Encoding.Latin1.GetBytes(Script)) };
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Commit(
            AgentWorkspace.Plan(Input, Patch([Frames(), AddCog(), invalid])), output));
        Assert.Equal(original, File.ReadAllBytes(Input));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_directory, ".sed-agent-*.tmp"));
    }

    [Fact]
    public void SwappedFramesNeedNewReviewedHashAndStaleSourceStillRefuses()
    {
        var first = AgentWorkspace.Plan(Input, Patch([Frames()], resources: false));
        var reversed = new { op = "setThingFrames", thing = 0, frames = new[] {
            new { position = new[] { 0.0, 0.0, 0.25 }, orientation = new[] { 0.0, 90.0, 0.0 } },
            new { position = new[] { 0.0, 0.0, 0.0 }, orientation = new[] { 0.0, 0.0, 0.0 } } } };
        var second = AgentWorkspace.Plan(Input, Patch([reversed], resources: false));
        string firstHash = Json(first.Report).GetProperty("outputSha256").GetString()!;
        string secondHash = Json(second.Report).GetProperty("outputSha256").GetString()!;
        Assert.NotEqual(firstHash, secondHash);
        string output = Path.Combine(_directory, "reversed.jkl");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(second, output, firstHash));
        Assert.False(File.Exists(output));
        AgentWorkspace.Commit(second, output, secondHash);
        var saved = AgentWorkspace.Load(output).Document.Level.Things[0];
        Assert.Equal(new[] { "(0/0/0.25:0/90/0)", "(0/0/0:0/0/0)" },
            saved.SourceParameters.Where(p => p.Key == "frame").Select(p => p.Value));
        File.AppendAllText(Input, "\n# unrelated source change\n");
        string stale = Path.Combine(_directory, "stale-source.jkl");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(first, stale, firstHash));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void NewScriptBasenamesFitLegacyNameFieldWithoutRestrictingParser()
    {
        var doc = AgentWorkspace.Load(Input).Document;
        string valid = new string('a', 27) + ".cog";
        var validScript = CogScript.Parse(valid, "symbols\nmessage startup\nend\n");
        Assert.Equal(valid, CogBindings.PrepareAdd(doc, valid, validScript, [], ExistingLocalScript).Cog.Name);
        foreach (string name in new[] { new string('a', 28) + ".cog", "sub/path.cog", "sub\\path.cog", "x:path.cog", "bad.cog " })
        {
            var parsed = CogScript.Parse(name, "symbols\nmessage startup\nend\n");
            Assert.True(parsed.IsComplete); // historical reads remain available
            Assert.Equal("invalidCogScriptName", Assert.Throws<CogBindingException>(() => CogBindings.PrepareAdd(doc, name, parsed, [])).Code);
        }
        var schema = Json(AgentSchema.Patch("addCog")).GetProperty("properties").GetProperty("operations")
            .GetProperty("items").GetProperty("oneOf")[0].GetProperty("properties").GetProperty("script");
        Assert.Equal(31, schema.GetProperty("maxLength").GetInt32());
    }

    [Fact]
    public void ScriptAndArchiveStalenessRefuseWithoutOutput()
    {
        var plan = AgentWorkspace.Plan(Input, Patch([AddCog()]));
        WriteScript(Script + "\n# changed\n");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(Input, Patch([AddCog()])));
        string output = Path.Combine(_directory, "refused.jkl");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, output));
        Assert.False(File.Exists(output));
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(Input, Patch([AddCog()], resources: false)));
    }

    [Theory]
    [InlineData("future target")]
    [InlineData("thing target\nthing TARGET")]
    public void IncompleteScriptSchemaRefuses(string declarations)
    {
        string script = "symbols\n" + declarations + "\nend\n";
        WriteScript(script);
        var add = new { op = "addCog", script = "play01.cog", scriptSha256 = AgentWorkspace.Hash(Encoding.Latin1.GetBytes(script)),
            bindings = new[] { new { symbol = "target", kind = "thing", targetIndex = 0 } } };
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(Input, Patch([add])));
    }

    [Fact]
    public void FrameInputBudgetAndNestedErrorsAreDiscoverable()
    {
        byte[] before = File.ReadAllBytes(Input);
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(Input, Patch([Frames(1_000_001)], resources: false)));
        Assert.Equal("/operations/0/frames/1/position/2", error.JsonPointer);
        Assert.Equal(before, File.ReadAllBytes(Input));
        var schema = Json(AgentSchema.Patch("setThingFrames"));
        var frames = schema.GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("oneOf")[0].GetProperty("properties").GetProperty("frames");
        Assert.Equal(SetThingFramesCommand.MaxFrames, frames.GetProperty("maxItems").GetInt32());
        Assert.Contains("addCog", Json(AgentSchema.Patch("addCog")).GetProperty("allOf").GetRawText());
    }

    [Fact]
    public void NewFrameAuthoringDoesNotRelaxGenericRepeatedValueRefusal()
    {
        Assert.Throws<InvalidOperationException>(() => AgentWorkspace.Plan(Input, Patch([
            new { op = "setThingValue", thing = 0, key = "frame", value = "(0/0/2:0/0/0)" }], resources: false)));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

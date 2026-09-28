using System.Text;
using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Cogs;
using Sed.Formats.Gob;
using Sed.Formats.Jed;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed partial class PlayableAuthoringTests
{
    [Fact]
    public void CogAppendNeverCapturesAnExistingUncheckedReference()
    {
        var doc = AgentWorkspace.Load(Input).Document;
        doc.Level.Cogs[0].Values.Add("1");
        File.WriteAllText(Input, JklWriter.Build(doc));
        WriteScript(Script, "symbols\ncog future\nend\n");
        byte[] original = File.ReadAllBytes(Input);
        string output = Path.Combine(_directory, "must-not-exist.jkl");
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Commit(
            AgentWorkspace.Plan(Input, Patch([AddCog()])), output));
        Assert.Contains("existingCogReferenceCapture", error.Message);
        Assert.Contains("COG 0, parameter 0 ('future')", error.Message);
        Assert.Contains("UnplacedUnchecked would become Level", error.Message);
        Assert.Equal(0, error.OperationIndex);
        Assert.Equal(original, File.ReadAllBytes(Input));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData(null, "incompleteExistingCogSchema")]
    [InlineData("symbols\nfuture x\nend\n", "incompleteExistingCogSchema")]
    [InlineData("symbols\ncog x\nend\n", "existingCogParameterCountMismatch")]
    public void ExistingCogClosureCannotGuessMissingIncompleteOrMismatchedSchemas(string? master, string expected)
    {
        if (master is null)
            GobWriter.Build(Archive, [("cog/play01.cog", Encoding.Latin1.GetBytes(Script))]);
        else WriteScript(Script, master);
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(Input, Patch([AddCog()])));
        Assert.Contains(expected, error.Message);
        Assert.Contains("Existing COG 0", error.Message);
    }

    [Theory]
    [InlineData("cog future", "32768")]
    [InlineData("cog future", "-1")]
    [InlineData("cog future", "0")]
    [InlineData("int numeric", "1")]
    public void AppendPreservesUnchangedReferenceClassesWithoutGuessingRawIntegers(string declaration, string value)
    {
        var doc = AgentWorkspace.Load(Input).Document;
        doc.Level.Cogs[0].Values.Add(value);
        var oldScript = CogScript.Parse("master.cog", "symbols\n" + declaration + "\nend\n");
        var before = CogBindings.Describe(doc, 0, oldScript).Rows[0].Target;
        var command = CogBindings.PrepareAdd(doc, "empty.cog", ExistingLocalScript("empty.cog"), [], _ => oldScript);
        command.Apply();
        Assert.Equal(value, doc.Level.Cogs[0].Values[0]);
        Assert.Equal(before, CogBindings.Describe(doc, 0, oldScript).Rows[0].Target);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("9")]
    public void UnchangedUncheckedClassificationDoesNotProveRuntimeCogIdentity(string value)
    {
        var doc=AgentWorkspace.Load(Input).Document;
        doc.Level.Cogs[0].Values.Add(value);
        var schema=CogScript.Parse("master.cog","symbols\ncog future\nend\n");
        Assert.Equal(CogBindingTargetStatus.UnplacedUnchecked,CogBindings.Describe(doc,0,schema).Rows[0].Target.Status);
        var error=Assert.Throws<CogBindingException>(()=>CogBindings.PrepareAdd(doc,"empty.cog",
            ExistingLocalScript("empty.cog"),[],_=>schema));
        Assert.Equal("existingCogRuntimeReference",error.Code);
        Assert.Contains("can shift that allocation",error.Message);
        Assert.Contains("COG 0, parameter 0 ('future')",error.Message);
        Assert.Single(doc.Level.Cogs); Assert.Equal(value,doc.Level.Cogs[0].Values[0]);
    }

    [Fact]
    public void ExistingScriptClosureIsHashBoundAndRequiredBySharedPreparation()
    {
        var plan = AgentWorkspace.Plan(Input, Patch([AddCog()]));
        WriteScript(Script, "symbols\n# closure bytes changed\nend\n");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, Path.Combine(_directory, "stale-closure.jkl")));
        Assert.False(File.Exists(Path.Combine(_directory, "stale-closure.jkl")));
        var doc = AgentWorkspace.Load(Input).Document;
        Assert.Equal("incompleteExistingCogSchema", Assert.Throws<CogBindingException>(() =>
            CogBindings.PrepareAdd(doc, "empty.cog", ExistingLocalScript("empty.cog"), [])).Code);
    }

    [Fact]
    public void SharedCogAppendUndoRestoresAllNumberingWithoutCanonicalizingUnrelatedObjects()
    {
        var doc = AgentWorkspace.Load(Input).Document;
        doc.Level.Cogs[0].Num = 17;
        var second = new Cog { Num = -23, Name = "second.cog" }; doc.Level.Cogs.Add(second);
        var added = new Cog { Num = 91, Name = "added.cog" };
        var before = doc.Level.Cogs.Select(c => (c, c.Num, c.Name, string.Join("|", c.Values))).ToArray();
        var history = new EditHistory(); history.Do(new CreateCogCommand(doc.Level, added));
        Assert.Equal(new[] {17, -23, 2}, doc.Level.Cogs.Select(c => c.Num));
        history.Undo();
        Assert.Equal(before, doc.Level.Cogs.Select(c => (c, c.Num, c.Name, string.Join("|", c.Values))));
        Assert.Equal(91, added.Num);
        history.Redo(); Assert.Equal(new[] {17, -23, 2}, doc.Level.Cogs.Select(c => c.Num));
    }

    [Fact]
    public void FullEffectiveParameterProofPreservesMixedRepeatedRecordsAndDictionaryOnlyLaterEdit()
    {
        var doc = AgentWorkspace.Load(Input).Document; var thing = doc.Level.Things[0];
        thing.SourceParameters.Clear(); thing.Values.Clear();
        KeyValuePair<string,string>[] original = [new("Health", "100"), new("numframes", "2"),
            new("frame", "(0/0/0:0/0/0)"), new("user0", "a"), new("health", "90"),
            new("frame", "(0/0/0:0/0/0)"), new("USER0", "b"), new("mass", "2")];
        thing.SourceParameters.AddRange(original);
        foreach (var pair in original) thing.Values[pair.Key] = pair.Value;
        File.WriteAllText(Input, JklWriter.Build(doc));
        var identical = new { op = "setThingFrames", thing = 0, frames = new[] {
            new { position = new[] {0,0,0}, orientation = new[] {0,0,0} },
            new { position = new[] {0,0,0}, orientation = new[] {0,0,0} } } };
        var plan = AgentWorkspace.Plan(Input, Patch([identical,
            new { op = "setThingValue", thing = 0, key = "addedOnly", value = "retained" }], resources: false));
        var reopened = JklParser.ParseDocument(plan.OutputText).Level.Things[0];
        KeyValuePair<string,string>[] expected = [new("Health", "100"), new("numframes", "2"),
            new("frame", "(0/0/0:0/0/0)"), new("frame", "(0/0/0:0/0/0)"),
            new("user0", "a"), new("health", "90"), new("USER0", "b"), new("mass", "2"), new("addedOnly", "retained")];
        Assert.Equal(expected, reopened.SourceParameters);
        Assert.Equal("90", reopened.Values["health"]); Assert.Equal("b", reopened.Values["user0"]);
        Assert.Equal("(0/0/0:0/0/0)", reopened.Values["frame"]); Assert.Equal("retained", reopened.Values["addedOnly"]);
        var state = Json(plan.Report).GetProperty("changes")[0].GetProperty("serializedFinal").GetProperty("state");
        var observed = state.GetProperty("effectiveParameters").EnumerateArray().Select(p =>
            new KeyValuePair<string,string>(p.GetProperty("key").GetString()!,p.GetProperty("value").GetString()!));
        Assert.Equal(expected, observed);
        Assert.Equal(reopened.Values.Count, state.GetProperty("values").GetArrayLength());
    }

    [Theory]
    [InlineData("SED 0.3.0")]
    [InlineData("JED 0.95")]
    [InlineData("NED 0.95")]
    public void ImportedNativeFramesPreserveAllUnrelatedRecordsWithInheritedPathMove(string signature)
    {
        string native = signature + "\nSEC: MOTS\nEND\nSEC: HEADER\nHDR 1 4 15 2 3 dflt.cmp 320\n" +
            "HOR 100 768 0 0\nCEIL 0 0\nMIP 1 2 3 4\nLOD 0.1 0.2 0.3 0.4\nEND\nSEC: GEO\nSECS 1\n" +
            "dflt.cmp none 0 0 0 0 0 0 0 0\nVXS 3\n0 0 0\n1 0 0\n0 1 0\nSURFS 1\n" +
            "none 4 0 4 3 3 0 3 -1 -1 0 1 1\n0 0 0 0 1 1 0 0 2 0 1 0 0 0 0 0 0 0 0 0 0\nEND\n" +
            "SEC: THINGS\nTHS 1\nmover 0 0 0 0 0 0 0 0\nHealth=100 frame=(0/0/0:0/0/0) health=90 user0=a frame=(0/0/1:0/0/0) USER0=b\nEND\n";
        var template = new Template {Name="mover",Parent="none"}; template.Values["move"]="path";
        var doc = JedImporter.ImportText(native, [template]).Document; var thing=Assert.Single(doc.Level.Things);
        Assert.False(thing.Values.ContainsKey("move"));
        var unrelated = thing.SourceParameters.Where(p => !p.Key.Equals("frame", StringComparison.OrdinalIgnoreCase)).ToArray();
        new SetThingFramesCommand(doc.Level, thing, [new(Vec3.Zero,Vec3.Zero), new(new(0,0,.5),Vec3.Zero)]).Apply();
        thing.Values["dictionaryOnly"]="kept";
        var reopened=JklParser.ParseDocument(JklWriter.Build(doc)).Level.Things[0];
        Assert.Equal(unrelated, reopened.SourceParameters.Where(p => p.Key is not ("frame" or "numframes" or "dictionaryOnly")));
        Assert.Equal("90",reopened.Values["health"]); Assert.Equal("b",reopened.Values["user0"]);
        Assert.Equal("kept",reopened.Values["dictionaryOnly"]); Assert.Equal("2",reopened.Values["numframes"]);
        Assert.Equal("(0/0/0.5:0/0/0)",reopened.Values["frame"]);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void IncompatibleFinalMoveOrTemplateRefusesInEitherOrder(bool template, bool changeFirst)
    {
        var doc = AgentWorkspace.Load(Input).Document;
        var physics = new Template {Name="not_path",Parent="none"}; physics.Values["move"]="physics";
        doc.Level.Templates.Add(physics.Name,physics); File.WriteAllText(Input,JklWriter.Build(doc));
        object change = template ? new {op="setThingTemplate",thing=0,template="not_path"} :
            new {op="setThingValue",thing=0,key="move",value="physics"};
        var operations = changeFirst ? new[] {change,Frames()} : new[] {Frames(),change};
        Assert.Throws<AgentRequestException>(()=>AgentWorkspace.Plan(Input,Patch(operations,resources:false)));
    }

    [Theory]
    [InlineData("numframes", "3")]
    [InlineData("frame", "garbage")]
    [InlineData("frame", "(NaN/0/0:0/0/0)")]
    [InlineData("frame", "(1000001/0/0:0/0/0)")]
    public void LaterGenericEditsCannotInvalidateAuthoredFrameRecords(string key, string value)
    {
        var single=new {op="setThingFrames",thing=0,frames=new[] {
            new {position=new[] {0,0,0},orientation=new[] {0,0,0}}}};
        var error=Assert.Throws<AgentRequestException>(()=>AgentWorkspace.Plan(Input,Patch([
            single,new {op="setThingValue",thing=0,key,value}],resources:false)));
        Assert.Equal(0,error.OperationIndex);
        Assert.Contains("Final state of frame-authored thing 0",error.Message);
    }

    [Fact]
    public void FinalEligibilityAllowsAnExplicitLaterRepairAndValidFrameReplacement()
    {
        var plan=AgentWorkspace.Plan(Input,Patch([Frames(),
            new {op="setThingValue",thing=0,key="move",value="physics"},
            new {op="setThingValue",thing=0,key="move",value="path"},Frames(.75)],resources:false));
        var saved=JklParser.ParseDocument(plan.OutputText).Level.Things[0];
        Assert.Equal("path",saved.Values["move"]);
        Assert.Equal("(0/0/0.75:0/90/0)",saved.Values["frame"]);
    }

    [Theory]
    [InlineData("bindThingArchitecture", false)] [InlineData("bindThingArchitecture", true)]
    [InlineData("rebindThingArchitecture", false)] [InlineData("rebindThingArchitecture", true)]
    [InlineData("bakeThingArchitecture", false)] [InlineData("bakeThingArchitecture", true)]
    public void PathFramesAndArchitectureCannotLeaveAnIncompatibleFinalThing(string operation, bool architectureFirst)
    {
        var doc=AgentWorkspace.Load(Input).Document; var thing=doc.Level.Things[0];
        thing.Values["model3d"]="synthetic.3do"; thing.Values["thingflags"]="0x8";
        var samples=new ArchitecturalLightObject(); var mesh=new ArchitecturalLightMesh();
        mesh.Vertices.AddRange(new ArchitecturalLightVertex[3]); samples.Meshes.Add(mesh); doc.Level.ArchitecturalLighting.Add(samples);
        File.WriteAllText(Input,JklWriter.Build(doc));
        GobWriter.Build(Archive,[("3do/synthetic.3do",Encoding.ASCII.GetBytes(ArchitectureTopologyFixture.Build([[3]])))]);
        object architecture=operation switch {
            "bindThingArchitecture"=>new {op=operation,thing=0,lightingIndex=0},
            "rebindThingArchitecture"=>new {op=operation,thing=0,policy="reset"},
            _=>new {op=operation,thing=0,profile="source-points-v1"}};
        // Prove the ARCH side is eligible alone: refusals below must be about
        // composition, not a missing model, allocation, resource or geometry flag.
        Assert.NotNull(AgentWorkspace.Plan(Input,Patch([architecture])));
        object[] operations=architectureFirst ? [architecture,Frames()] : [Frames(),architecture];
        byte[] original=File.ReadAllBytes(Input);
        Assert.Throws<AgentRequestException>(()=>AgentWorkspace.Plan(Input,Patch(operations)));
        Assert.Equal(original,File.ReadAllBytes(Input));
        if (!architectureFirst)
        {
            var repaired=AgentWorkspace.Plan(Input,Patch([Frames(),architecture,
                new {op="clearThingArchitecture",thing=0}]));
            Assert.Equal(-1,JklParser.ParseDocument(repaired.OutputText).Level.Things[0].ArchLightingIndex);
        }
    }
}

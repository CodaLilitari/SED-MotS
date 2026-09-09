using System.Text;
using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jed;
using Sed.Formats.Jkl;
using Xunit;

namespace Sed.Core.Tests;

public sealed class AgentSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-agent-safety-" + Guid.NewGuid().ToString("N"));
    public AgentSafetyTests() => Directory.CreateDirectory(_directory);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private string Room(string name = "room.jkl", ProjectType game = ProjectType.MysteriesOfTheSith, bool thing = false)
    {
        var level = LevelFactory.CreateUntexturedRoom(game);
        if (thing)
        {
            var template = new Template { Name = "auditthing", Parent = "none" };
            template.Values["type"] = "ghost"; level.Templates[template.Name] = template;
            level.Things.Add(new Thing { Level = level, Sector = level.Sectors[0], Template = template.Name, Name = "object" });
        }
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, JklWriter.Build(new JklDocument(level)));
        return path;
    }
    private static string Patch(string path, string operations, bool includeRevision = true)
    {
        var snapshot = AgentWorkspace.LoadWithProvenance(path);
        string revision = includeRevision ? ",\"sourceRevision\":\"" + snapshot.SourceRevision + "\"" : "";
        return "{\"schemaVersion\":1,\"inputSha256\":\"" + snapshot.InputSha256 + "\"" + revision + ",\"operations\":" + operations + "}";
    }
    private string Native()
    {
        string path = Path.Combine(_directory, "native.jed");
        File.WriteAllText(path, "SED 0.3.0\nSEC: MOTS\nEND\nSEC: HEADER\nHDR 1 4 15 2 3 - 320\nHOR 100 768 0 0\nCEIL 0 0\nMIP 1 2 3 4\nLOD 0.1 0.2 0.3 0.4\nEND\n");
        return path;
    }
    private const string Gravity = "[{\"op\":\"setGravity\",\"value\":6}]";

    [Fact]
    public void JklVersionOnePatchesRemainCompatibleWithoutSourceRevision()
    {
        string path = Room();
        var snapshot = AgentWorkspace.LoadWithProvenance(path);
        Assert.Equal(snapshot.InputSha256, snapshot.SourceRevision);
        Assert.Empty(snapshot.Dependencies);
        var plan = AgentWorkspace.Plan(path, Patch(path, Gravity, false));
        Assert.Equal(6, JklParser.ParseDocument(plan.OutputText).Level.Header.Gravity);
    }

    [Fact]
    public void NativePatchRequiresRevisionAndReportsMissingDependencies()
    {
        string path = Native();
        var snapshot = AgentWorkspace.LoadWithProvenance(path);
        Assert.Equal(2, snapshot.Dependencies.Count);
        Assert.Contains(snapshot.Dependencies, d => d.Role == "companionTemplates" && !d.Exists && d.Bytes is null && d.Sha256 is null);
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(path, Patch(path, Gravity, false)));
        Assert.Equal("/sourceRevision", error.JsonPointer);
        Assert.Equal(snapshot.SourceRevision, Json(AgentWorkspace.Inspect(path)).GetProperty("sourceRevision").GetString());
        Assert.Equal(snapshot.SourceRevision, Json(AgentWorkspace.Validate(path)).GetProperty("sourceRevision").GetString());
    }

    [Fact]
    public void NativeCompanionChangesRevisionWithoutChangingRawSourceHash()
    {
        string path = Native(); string companion = Path.Combine(_directory, "mots.tpl");
        File.WriteAllText(companion, "auditthing none model3d=before.3do\n");
        var before = AgentWorkspace.LoadWithProvenance(path);
        string patch = Patch(path, Gravity);
        File.WriteAllText(companion, "auditthing none model3d=after.3do\n");
        var after = AgentWorkspace.LoadWithProvenance(path);
        Assert.Equal(before.InputSha256, after.InputSha256);
        Assert.NotEqual(before.SourceRevision, after.SourceRevision);
        Assert.Equal("before.3do", before.Document.Level.Templates["auditthing"].Values["model3d"]);
        Assert.Equal("after.3do", after.Document.Level.Templates["auditthing"].Values["model3d"]);
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(path, patch));
    }

    [Theory]
    [InlineData("SEC:  MOTS", false)]
    [InlineData("SEC:\tMOTS", false)]
    [InlineData("sec:\tmots # game", true)]
    [InlineData("SEC: MOTS", true)]
    public void NativeLibrarySelectionMatchesWhitespaceAndNewlineParsing(string section, bool crOnly)
    {
        string path = Native();
        string source = File.ReadAllText(path).Replace("SEC: MOTS", section);
        if (crOnly) source = source.Replace('\n', '\r');
        File.WriteAllText(path, source);
        File.WriteAllText(Path.Combine(_directory, "mots.tpl"), "libraryprobe none model3d=mots.3do\n");
        File.WriteAllText(Path.Combine(_directory, "master.tpl"), "libraryprobe none model3d=jk.3do\n");
        var snapshot = AgentWorkspace.LoadWithProvenance(path);
        Assert.Equal(ProjectType.MysteriesOfTheSith, snapshot.Document.Level.Kind);
        Assert.Equal("mots.3do", snapshot.Document.Level.Templates["libraryprobe"].Values["model3d"]);
        Assert.Equal("mots.3do", JedImporter.ImportFile(path).Document.Level.Templates["libraryprobe"].Values["model3d"]);
        Assert.All(snapshot.Dependencies, dependency => Assert.EndsWith("mots.tpl", dependency.Path));
        string patch = Patch(path, Gravity);
        File.WriteAllText(Path.Combine(_directory, "mots.tpl"), "libraryprobe none model3d=changed.3do\n");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(path, patch));
    }

    [Fact]
    public void NativeGameDetectionRecognizesArchitectureAndIgnoresComments()
    {
        Assert.Equal(ProjectType.MysteriesOfTheSith, JedImporter.DetectProjectType("SED 0.3.0\rSEC:\tARCHLIGHTING\rEND\r"));
        Assert.Equal(ProjectType.JediKnight, JedImporter.DetectProjectType("JED 0.95\r# SEC: MOTS\rSEC: HEADER\r"));
    }

    [Theory]
    [InlineData("change")]
    [InlineData("delete")]
    [InlineData("appear")]
    public void NativeDependencyChangesBlockCapturedPlanCommit(string mutation)
    {
        string path = Native(); string companion = Path.Combine(_directory, "mots.tpl");
        if (mutation != "appear") File.WriteAllText(companion, "auditthing none model3d=before.3do\n");
        var plan = AgentWorkspace.Plan(path, Patch(path, Gravity));
        if (mutation == "delete") File.Delete(companion);
        else File.WriteAllText(companion, "auditthing none model3d=after.3do\n");
        string output = Path.Combine(_directory, "refused.jkl");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, output));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_directory, ".sed-agent-*.tmp"));
    }

    [Fact]
    public void NativeConversionCapturesDependenciesAndOutputHash()
    {
        string path = Native();
        var conversion = AgentWorkspace.ConvertDocument(path);
        Assert.Equal(AgentWorkspace.Hash(Encoding.UTF8.GetBytes(conversion.OutputText)), Json(conversion.Report).GetProperty("outputSha256").GetString());
        File.WriteAllText(Path.Combine(_directory, "mots.tpl"), "newtemplate none type=ghost\n");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(conversion, Path.Combine(_directory, "converted.jkl")));
    }

    [Fact]
    public void CapturedTemplateTextMergePreservesRepeatedValuesAndDoesNotReopenFiles()
    {
        var templates = JedImporter.LoadTemplateTexts(["base none model3d=base.3do\nitem base mass=1\n",
            "item base frame=(0/0/0:0/0/0) frame=(1/2/3:0/0/0) mass=2\n"]);
        var item = templates.Single(t => t.Name == "item");
        Assert.Equal("base", item.Parent); Assert.Equal("2", item.Values["mass"]);
        Assert.Equal(2, item.SourceParameters.Count(p => p.Key == "frame"));
        Assert.Contains(templates, t => t.Name == "base");
    }

    [Theory]
    [InlineData("[{\"op\":\"setGravity\",\"value\":6},{\"op\":\"setSectorFlags\",\"sector\":99,\"flags\":0}]", 1, "setSectorFlags", "/operations/1/sector")]
    [InlineData("[{\"op\":\"setThingPosition\",\"thing\":0,\"sector\":0,\"position\":[0,\"wrong\",0]}]", 0, "setThingPosition", "/operations/0/position/1")]
    [InlineData("[{\"op\":\"setGravity\",\"value\":2,\"extra/key\":1}]", 0, "setGravity", "/operations/0/extra~1key")]
    [InlineData("[{\"op\":\"setGravity\"}]", 0, "setGravity", "/operations/0/value")]
    public void OperationFailuresIdentifyOperationAndJsonPointer(string operations, int index, string operation, string pointer)
    {
        string path = Room(thing: true); var original = File.ReadAllBytes(path);
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(path, Patch(path, operations)));
        Assert.Equal(index, error.OperationIndex); Assert.Equal(operation, error.Operation); Assert.Equal(pointer, error.JsonPointer);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("[1,0,0]")]
    [InlineData("[0.5,0.5,0.5]")]
    public void JkRefusesUnrepresentableLightColor(string rgb)
    {
        string path = Room(game: ProjectType.JediKnight);
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(path, Patch(path,
            "[{\"op\":\"addLight\",\"position\":[0,0,0],\"rgb\":" + rgb + ",\"intensity\":1,\"range\":2}]")));
        Assert.Equal("/operations/0/rgb", error.JsonPointer);
        Assert.Equal(0, error.OperationIndex);
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, "[1,1,1]")]
    [InlineData(ProjectType.MysteriesOfTheSith, "[0.2,0.4,0.8]")]
    public void SupportedLightsReportAppendedIndexAndActualSerializedValues(ProjectType game, string rgb)
    {
        string path = Room(game: game);
        var plan = AgentWorkspace.Plan(path, Patch(path,
            "[{\"op\":\"addLight\",\"position\":[1,2,3],\"rgb\":" + rgb + ",\"intensity\":2,\"range\":3}]"));
        var effect = Json(plan.Report).GetProperty("changes")[0].GetProperty("serializedFinal");
        Assert.Equal("light", effect.GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal(0, effect.GetProperty("target").GetProperty("index").GetInt32());
        var state = effect.GetProperty("state");
        var reopened = JklParser.ParseDocument(plan.OutputText).Level.Lights[0];
        Assert.Equal(reopened.Color.B, state.GetProperty("rgb")[2].GetSingle());
        Assert.Equal(reopened.Intensity, state.GetProperty("intensity").GetDouble());
    }

    [Fact]
    public void CollisionFlagCannotInventOrRemoveInteriorBoxData()
    {
        string path = Room();
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(path, Patch(path,
            "[{\"op\":\"setSectorFlags\",\"sector\":0,\"flags\":4096}]")));
        Assert.Equal("/operations/0/flags", error.JsonPointer);
        var doc = AgentWorkspace.Load(path).Document;
        doc.Level.Sectors[0].HasCollideBox = true; doc.Level.Sectors[0].Flags |= 0x1000;
        doc.Level.Sectors[0].CollideBox = new(new(-0.25,-0.25,-0.25),new(0.25,0.25,0.25));
        File.WriteAllText(path,JklWriter.Build(doc));
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(path, Patch(path,
            "[{\"op\":\"setSectorFlags\",\"sector\":0,\"flags\":0}]")));
        var accepted = AgentWorkspace.Plan(path, Patch(path,"[{\"op\":\"setSectorFlags\",\"sector\":0,\"flags\":4100}]"));
        Assert.True(JklParser.ParseDocument(accepted.OutputText).Level.Sectors[0].HasCollideBox);
    }

    [Fact]
    public void SequentialSettersKeepIntermediateAndFinalEffectsDistinct()
    {
        string path = Room(thing:true);
        var plan = AgentWorkspace.Plan(path, Patch(path,
            "[{\"op\":\"setThingName\",\"thing\":0,\"name\":\"first\"},{\"op\":\"setThingName\",\"thing\":0,\"name\":\"second\"}]"));
        var changes = Json(plan.Report).GetProperty("changes");
        Assert.Equal("first",changes[0].GetProperty("afterOperation").GetProperty("state").GetProperty("name").GetString());
        Assert.Equal("second",changes[0].GetProperty("serializedFinal").GetProperty("state").GetProperty("name").GetString());
        Assert.Equal("second",changes[1].GetProperty("serializedFinal").GetProperty("state").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, "mono")]
    [InlineData(ProjectType.MysteriesOfTheSith, "mono")]
    [InlineData(ProjectType.MysteriesOfTheSith, "red")]
    public void VertexLightReadbackHandlesImplicitScalarRepresentation(ProjectType game,string channel)
    {
        string path=Room(game:game);
        var plan=AgentWorkspace.Plan(path,Patch(path,"[{\"op\":\"setVertexLight\",\"sector\":0,\"vertex\":0,\"channel\":\""+channel+"\",\"value\":0.3}]"));
        Assert.NotEmpty(Json(plan.Report).GetProperty("changes")[0].GetProperty("serializedFinal").GetProperty("state").GetProperty("corners").EnumerateArray());
    }

    [Fact]
    public void AppendedSelectorsCanBeUsedBySubsequentOperationsAndAreReported()
    {
        string path=Room();
        var plan=AgentWorkspace.Plan(path,Patch(path,"[{\"op\":\"addBox\",\"center\":[5,0,0],\"halfSize\":1},{\"op\":\"setSectorAmbient\",\"sector\":1,\"value\":0.4}]"));
        var changes=Json(plan.Report).GetProperty("changes");
        Assert.Equal(1,changes[0].GetProperty("serializedFinal").GetProperty("target").GetProperty("index").GetInt32());
        Assert.Equal(.4f,changes[1].GetProperty("serializedFinal").GetProperty("state").GetProperty("ambient")[0].GetSingle());
    }

    [Fact]
    public void DiagnosticsRetainDistinctThingIndices()
    {
        string path=Room(thing:true);var doc=AgentWorkspace.Load(path).Document;
        doc.Level.Things[0].Sector=null;
        doc.Level.Things.Add(new Thing {Level=doc.Level,Name="second",Template="auditthing"});
        File.WriteAllText(path,JklWriter.Build(doc));
        var warnings=Json(AgentWorkspace.Validate(path)).GetProperty("issues").EnumerateArray().ToArray();
        Assert.Equal(new[]{0,1},warnings.Where(w=>w.GetProperty("type").GetString()=="Thing").Select(w=>w.GetProperty("itemIndex").GetInt32()));
        Assert.All(warnings,w=>Assert.True(w.TryGetProperty("parameterIndex",out _)));
    }

    public void Dispose() => Directory.Delete(_directory,recursive:true);
}

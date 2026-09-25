using System.Text;
using System.Text.Json;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

public sealed class AgentArchitectureAuthoringTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-arch-authoring-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    private readonly string _archive;
    public AgentArchitectureAuthoringTests()
    {
        Directory.CreateDirectory(_directory);
        _input = Path.Combine(_directory, "input.jkl");
        _archive = Path.Combine(_directory, "models.gob");
        AgentWorkspace.Create(_input, "mots");
        var doc = AgentWorkspace.Load(_input).Document;
        var template = new Template { Name = "architecture", Parent = "none" };
        template.Values["model3d"] = "synthetic.3do";
        template.Values["thingflags"] = "0x8";
        doc.Level.Templates.Add(template.Name, template);
        var thing = doc.Level.NewThing();
        thing.Template = template.Name; thing.Name = "test"; thing.Sector = doc.Level.Sectors[0];
        doc.Level.Lights.Add(new Light { Position = new Vec3(0, 0, 1), Range = 2, Intensity = .8,
            RgbIntensity = .4, Color = new ColorF(1, .5f, .25f) });
        File.WriteAllText(_input, JklWriter.Build(doc));
        WriteModel(ArchitectureTopologyFixture.Build([[3]]));
    }
    private void WriteModel(string model) => GobWriter.Build(_archive, [("3do/synthetic.3do", Encoding.ASCII.GetBytes(model))]);
    private string Request(string operations, string? path = null, bool resources = true, string? revision = null)
    {
        var root = new Dictionary<string, object> { ["schemaVersion"] = 1,
            ["inputSha256"] = AgentWorkspace.Load(path ?? _input).Hash,
            ["operations"] = JsonSerializer.Deserialize<JsonElement>(operations) };
        if (resources) root["resourceArchives"] = new[] { new ResourceArchiveRequest(_archive, AgentWorkspace.Hash(File.ReadAllBytes(_archive))) };
        if (revision is not null) root["resourceRevision"] = revision;
        return JsonSerializer.Serialize(root, AgentWorkspace.JsonOptions);
    }
    private const string Bake = """[{"op":"bakeThingArchitecture","thing":0,"profile":"source-points-v1"}]""";
    private string Save(PreparedAgentEdit plan, string name) { string path = Path.Combine(_directory, name); AgentWorkspace.Commit(plan, path); return path; }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);

    [Theory]
    [InlineData("source-points-v1")]
    [InlineData("source-points-world-occluded-v1")]
    public void BakeBootstrapsAndReadbackKeepsAnalyticChannelsAndResourceBoundary(string profile)
    {
        var original = File.ReadAllBytes(_input);
        var plan = AgentWorkspace.Plan(_input, Request(Bake.Replace("source-points-v1", profile)));
        string path = Save(plan, "baked.jkl");
        var level = AgentWorkspace.Load(path).Document.Level;
        var sample = Assert.Single(Assert.Single(level.ArchitecturalLighting).Meshes).Vertices[0];
        Assert.Equal(.2, sample.Mono, 12); Assert.Equal(.1, sample.Red, 12);
        Assert.Equal(.05, sample.Green, 12); Assert.Equal(.025, sample.Blue, 12);
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(level, level.Things[0]));
        var thing = Json(AgentWorkspace.Query(path, """{"schemaVersion":1,"collection":"things"}""" )).GetProperty("items")[0];
        Assert.Equal(profile, thing.GetProperty("architecture").GetProperty("provenance").GetProperty("origin").GetString());
        Assert.Equal("unverified-use-resource-validation", thing.GetProperty("architecture").GetProperty("resourceStatus").GetString());
        var page = Json(AgentWorkspace.Query(path, """{"schemaVersion":1,"collection":"architecturalVertices","lightingIndex":0,"mesh":0,"limit":1}"""));
        Assert.Equal(3, page.GetProperty("total").GetInt32()); Assert.Equal(1, page.GetProperty("nextOffset").GetInt32());
        Assert.Equal(.2, page.GetProperty("items")[0].GetProperty("mono").GetDouble(), 12);
        Assert.Equal(original, File.ReadAllBytes(_input));
        Assert.NotNull(plan.ResourceRevision);
    }

    [Fact]
    public void SubsequentSourceEditSavesStaleBakeWithoutChangingSamples()
    {
        string baked = Save(AgentWorkspace.Plan(_input, Request(Bake)), "baked.jkl");
        var plan = AgentWorkspace.Plan(baked, Request("""[{"op":"setLight","light":0,"rgbIntensity":0}]""", baked, resources: false));
        var level = JklParser.ParseDocument(plan.OutputText).Level;
        Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(level, level.Things[0]));
        Assert.Equal(.1, level.ArchitecturalLighting[0].Meshes[0].Vertices[0].Red, 12);
        string stale = Save(plan, "stale.jkl");
        var refreshed = JklParser.ParseDocument(AgentWorkspace.Plan(stale, Request(Bake, stale)).OutputText).Level;
        Assert.Equal(2, refreshed.ArchitecturalLighting.Count);
        Assert.Equal(0, refreshed.ArchitecturalLighting[1].Meshes[0].Vertices[0].Red);
        Assert.Equal(.2, refreshed.ArchitecturalLighting[1].Meshes[0].Vertices[0].Mono, 12);
    }

    [Fact]
    public void ExplicitResetAndRebindingPreserveOldObjectsWithoutInventingBakeOrigin()
    {
        string baked = Save(AgentWorkspace.Plan(_input, Request(Bake)), "baked.jkl");
        var reset = AgentWorkspace.Plan(baked, Request("""[{"op":"rebindThingArchitecture","thing":0,"policy":"reset"}]""", baked));
        var level = JklParser.ParseDocument(reset.OutputText).Level;
        Assert.Equal(2, level.ArchitecturalLighting.Count);
        Assert.Equal("reset", ArchitecturalLightingAuthoring.Status(level, level.Things[0]));
        Assert.Equal(0, level.ArchitecturalLighting[1].Meshes[0].Vertices[0].Mono);
        var bound = AgentWorkspace.Plan(baked, Request("""[{"op":"clearThingArchitecture","thing":0},{"op":"bindThingArchitecture","thing":0,"lightingIndex":0}]""", baked));
        level = JklParser.ParseDocument(bound.OutputText).Level;
        Assert.Equal("bound", ArchitecturalLightingAuthoring.Status(level, level.Things[0]));
        Assert.Equal(level.ArchitecturalLighting[0].Meshes[0].Vertices, level.ArchitecturalLighting[1].Meshes[0].Vertices);
    }

    [Theory]
    [InlineData("""[{"op":"bakeThingArchitecture","thing":0,"profile":"retail"}]""")]
    [InlineData("""[{"op":"rebindThingArchitecture","thing":0,"policy":"keep"}]""")]
    [InlineData("""[{"op":"bindThingArchitecture","thing":0,"lightingIndex":0}]""")]
    public void RefusalsHaveOperationDiagnosticsAndLeaveSourceAlone(string operations)
    {
        var original = File.ReadAllBytes(_input);
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(_input, Request(operations)));
        Assert.Equal(0, error.OperationIndex); Assert.StartsWith("/operations/0", error.JsonPointer);
        Assert.Equal(original, File.ReadAllBytes(_input));
    }

    [Fact]
    public void MissingResourcesAndLateUnsupportedBakeNeverPublishPartialEdit()
    {
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(_input, Request(Bake, resources: false)));
        WriteModel(ArchitectureTopologyFixture.Build([[3], [2]]));
        var original = File.ReadAllBytes(_input);
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(_input,
            Request("""[{"op":"setGravity","value":6},{"op":"bakeThingArchitecture","thing":0,"profile":"source-points-v1"}]""")));
        Assert.Equal(1, error.OperationIndex); Assert.Equal(original, File.ReadAllBytes(_input));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void ModelMutationAfterPlanningAndReviewedRevisionAreIndependentlyGuarded()
    {
        var plan = AgentWorkspace.Plan(_input, Request(Bake));
        WriteModel("# raw bytes changed\n" + ArchitectureTopologyFixture.Build([[3]]));
        Assert.ThrowsAny<Exception>(() => AgentWorkspace.Commit(plan, Path.Combine(_directory, "refused.jkl")));
        Assert.False(File.Exists(Path.Combine(_directory, "refused.jkl")));
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(_input, Request(Bake, revision: plan.ResourceRevision)));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"collection":"architecturalVertices"}""")]
    [InlineData("""{"schemaVersion":1,"collection":"things","mesh":0}""")]
    [InlineData("""{"schemaVersion":1,"collection":"architecturalVertices","lightingIndex":0,"mesh":0}""")]
    public void SampleQueryRejectsMissingExtraneousAndInvalidAddresses(string request)
        => Assert.Throws<AgentRequestException>(() => AgentWorkspace.Query(_input, request));

    public void Dispose() => Directory.Delete(_directory, true);
}

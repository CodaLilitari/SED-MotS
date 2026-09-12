using System.Text;
using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;
using Sed.Formats.ThreeDo;
using Sed.Rendering;

namespace Sed.Core.Tests;

public sealed class AgentResourceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-resource-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    private readonly string _archive;
    private static string ModelText(int lowerVertices = 2) => ArchitectureTopologyFixture.Build([ [3], [lowerVertices] ]);

    public AgentResourceTests()
    {
        Directory.CreateDirectory(_directory);
        _input = Path.Combine(_directory, "source.jkl");
        _archive = Path.Combine(_directory, "models.goo");
        AgentWorkspace.Create(_input, "mots");
        var doc = AgentWorkspace.Load(_input).Document;
        var template = new Template { Name = "architecture" };
        template.Values["model3d"] = "synthetic.3do";
        template.Values["thingflags"] = "0x8";
        doc.Level.Templates[template.Name] = template;
        var thing = doc.Level.NewThing();
        thing.Template = template.Name; thing.Name = "original"; thing.Sector = doc.Level.Sectors[0];
        thing.ArchLightingIndex = 0; thing.ArchLightingSourceModel = "synthetic.3do";
        var architecture = new ArchitecturalLightObject();
        var mesh = new ArchitecturalLightMesh();
        for (int i = 0; i < 3; i++) mesh.Vertices.Add(new(.1, .2, .3, .4));
        architecture.Meshes.Add(mesh); doc.Level.ArchitecturalLighting.Add(architecture);
        File.WriteAllText(_input, JklWriter.Build(doc));
        WriteModel(ModelText());
    }

    private void WriteModel(string text) => GobWriter.Build(_archive, [("3do/synthetic.3do", Encoding.Latin1.GetBytes(text))]);
    private ResourceArchiveRequest Archive() => new(_archive, AgentWorkspace.Hash(File.ReadAllBytes(_archive)));
    private string Request(bool patch = true, string? revision = null, bool includeResources = true, string? operations = null)
    {
        var root = new Dictionary<string, object> { ["schemaVersion"] = 1, ["inputSha256"] = AgentWorkspace.Load(_input).Hash };
        if (includeResources) root["resourceArchives"] = new[] { Archive() };
        if (revision is not null) root["resourceRevision"] = revision;
        if (patch) root["operations"] = JsonSerializer.Deserialize<JsonElement>(operations ?? """[{"op":"setGravity","value":6}]""");
        return JsonSerializer.Serialize(root, AgentWorkspace.JsonOptions);
    }
    private static JsonElement Report(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);

    [Fact]
    public void PlanAndApplyBindAllLodsAndKeepMissingHistoryExplicit()
    {
        byte[] original = File.ReadAllBytes(_input);
        var plan = AgentWorkspace.Plan(_input, Request());
        var result = Report(plan.Report);
        Assert.Equal("compatible-current-resources", result.GetProperty("architectureValidation").GetProperty("status").GetString());
        var binding = result.GetProperty("architectureValidation").GetProperty("bindings")[0];
        Assert.True(binding.GetProperty("canUseBinding").GetBoolean());
        Assert.False(binding.GetProperty("historicalBaselineVerified").GetBoolean());
        Assert.Equal(64, plan.ResourceRevision!.Length);
        var reviewed = AgentWorkspace.Plan(_input, Request(revision: plan.ResourceRevision));
        string output = Path.Combine(_directory, "reviewed.jkl");
        AgentWorkspace.Commit(reviewed, output, result.GetProperty("outputSha256").GetString());
        Assert.Equal(6, AgentWorkspace.Load(output).Document.Level.Header.Gravity);
        Assert.Equal(original, File.ReadAllBytes(_input));
    }

    [Fact]
    public void ResourceFreeEditsRemainAvailableButDoNotClaimValidation()
    {
        var plan = AgentWorkspace.Plan(_input, Request(includeResources: false));
        var validation = Report(plan.Report).GetProperty("architectureValidation");
        Assert.Equal("unverified-no-resources", validation.GetProperty("status").GetString());
        Assert.Empty(validation.GetProperty("bindings").EnumerateArray());
        Assert.Null(plan.ResourceRevision);
    }

    [Fact]
    public void SameOutputWithNewArchiveBytesCannotReuseReviewedResourceRevision()
    {
        var plan = AgentWorkspace.Plan(_input, Request());
        WriteModel(ModelText().Replace("0.25", "0.5"));
        var current = AgentWorkspace.Plan(_input, Request());
        Assert.Equal(plan.OutputText, current.OutputText);
        Assert.NotEqual(plan.ResourceRevision, current.ResourceRevision);
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(_input, Request(revision: plan.ResourceRevision)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedOrRemovedArchiveAfterPlanningPreventsOutput(bool remove)
    {
        var plan = AgentWorkspace.Plan(_input, Request());
        if (remove) File.Delete(_archive); else WriteModel(ModelText().Replace("0.25", "0.5"));
        string output = Path.Combine(_directory, "refused.jkl");
        Assert.ThrowsAny<Exception>(() => AgentWorkspace.Commit(plan, output));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void ChangingPreparedOutputCannotReuseCapturedValidation()
    {
        var plan = AgentWorkspace.Plan(_input, Request());
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan with { OutputText = plan.OutputText + "\n# changed\n" }, Path.Combine(_directory, "changed.jkl")));
    }

    [Fact]
    public void LowerLodOverflowIsRejectedByValidationPlanConversionAndPreview()
    {
        WriteModel(ModelText(4));
        string resources = Request(patch: false);
        var validation = Report(AgentWorkspace.Validate(_input, resources));
        Assert.False(validation.GetProperty("valid").GetBoolean());
        Assert.Throws<AgentValidationException>(() => AgentWorkspace.Plan(_input, Request()));
        Assert.Throws<AgentValidationException>(() => AgentWorkspace.ConvertDocument(_input, resources));
        var level = AgentWorkspace.Load(_input).Document.Level;
        var model = ThreeDoParser.Parse(ModelText(4));
        var assembler = new SceneAssembler();
        assembler.AddThings(level, _ => model);
        Assert.Contains(assembler.Build().Diagnostics, d => d.Contains("Lower LOD", StringComparison.Ordinal));
        Assert.Equal(0, level.Things[0].ArchLightingIndex);
        Assert.Equal(3, level.ArchitecturalLighting[0].Meshes[0].Vertices.Count);
    }

    [Fact]
    public void ExplicitClearCanRepairAnUnresolvableBindingWithoutDiscardingStoredArrays()
    {
        GobWriter.Build(_archive, [("3do/different.3do", Encoding.ASCII.GetBytes(ModelText()))]);
        Assert.Throws<AgentValidationException>(() => AgentWorkspace.Plan(_input, Request()));
        var plan = AgentWorkspace.Plan(_input, Request(operations: """[{"op":"clearThingArchitecture","thing":0}]"""));
        var level = JklParser.ParseDocument(plan.OutputText).Level;
        Assert.Equal(-1, level.Things[0].ArchLightingIndex);
        Assert.Single(level.ArchitecturalLighting);
        Assert.Equal("not-applicable", Report(plan.Report).GetProperty("architectureValidation").GetProperty("status").GetString());
    }

    [Fact]
    public void NativeFingerprintIsCheckedBeforeJklSerializationDropsIt()
    {
        var doc = AgentWorkspace.Load(_input).Document;
        doc.Level.Things[0].ArchLightingSourceModelHash = ArchitectureModelFingerprint.Compute(ModelText())!;
        using (var resources = new ExplicitArchiveResources([Archive()]))
        {
            var export = ResourceValidatedExport.Prepare(doc, resources);
            Assert.True(Assert.Single(export.Validation.Bindings).HistoricalBaselineVerified);
            Assert.Empty(JklParser.ParseDocument(export.OutputText).Level.Things[0].ArchLightingSourceModelHash);
        }
        WriteModel(ModelText().Replace("0.25", "0.5"));
        using var changed = new ExplicitArchiveResources([Archive()]);
        Assert.Throws<AgentValidationException>(() => ResourceValidatedExport.Prepare(doc, changed));
    }

    [Fact]
    public void MissingReadsStayNullAndAmbiguousResourceDoesNotFallThrough()
    {
        GobWriter.Build(_archive, [("a/synthetic.3do", Encoding.ASCII.GetBytes(ModelText())),
            ("b/synthetic.3do", Encoding.ASCII.GetBytes(ModelText()))]);
        string lower = Path.Combine(_directory, "lower.goo");
        GobWriter.Build(lower, [("3do/synthetic.3do", Encoding.ASCII.GetBytes(ModelText()))]);
        using var resources = new ExplicitArchiveResources([Archive(), new(lower, AgentWorkspace.Hash(File.ReadAllBytes(lower)))]);
        Assert.Null(resources.Read("missing.mat", "mat"));
        Assert.Null(resources.ResolveArchitectureModel("synthetic.3do"));
        Assert.Contains(resources.Diagnostics, d => d.StartsWith("Ambiguous", StringComparison.Ordinal));
        Assert.Empty(resources.UsedResources);
    }

    [Fact]
    public void ExactResourceDirectoryHasPriorityAndEveryArchiveOrderIsBound()
    {
        GobWriter.Build(_archive, [("other/synthetic.3do", Encoding.ASCII.GetBytes(ModelText(4))),
            ("3do/synthetic.3do", Encoding.ASCII.GetBytes(ModelText()))]);
        string lower = Path.Combine(_directory, "lower.goo");
        GobWriter.Build(lower, [("3do/synthetic.3do", Encoding.ASCII.GetBytes(ModelText(4)))]);
        var second = new ResourceArchiveRequest(lower, AgentWorkspace.Hash(File.ReadAllBytes(lower)));
        using var firstOrder = new ExplicitArchiveResources([Archive(), second]);
        using var otherOrder = new ExplicitArchiveResources([second, Archive()]);
        var doc = AgentWorkspace.Load(_input).Document;
        Assert.False(ArchitectureResourceValidation.Evaluate(doc.Level, firstOrder).HasErrors);
        Assert.True(ArchitectureResourceValidation.Evaluate(doc.Level, otherOrder).HasErrors);
        Assert.NotEqual(firstOrder.ResourceRevision, otherOrder.ResourceRevision);
    }

    [Fact]
    public void RevisionWithoutArchivesAndDuplicateManifestFieldsAreRefused()
    {
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(_input, Request(revision: new string('a', 64), includeResources: false)));
        using var duplicate = JsonDocument.Parse("""[{"path":"a","path":"b","sha256":"c"}]""");
        Assert.Throws<AgentRequestException>(() => ResourceManifest.Parse(duplicate.RootElement));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

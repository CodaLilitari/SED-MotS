using System.Text;
using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;
using Sed.Formats.ThreeDo;

namespace Sed.Core.Tests;

public sealed class ArchitectureAuthoringFormatTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-arch-authoring-" + Guid.NewGuid().ToString("N"));
    private readonly string _archive;
    private const string RootRow = "0: 0x0 0x10 0 -1 -1 -1 0 0 0 0 0 0 0 0 0 0 root";
    private static string ModelText => """
        # Original synthetic triangle, no game assets.
        SECTION: HEADER
        3DO 2.2
        SECTION: MODELRESOURCE
        MATERIALS 0
        SECTION: GEOMETRYDEF
        RADIUS 2
        INSERT OFFSET 0 0 0
        GEOSETS 1
        GEOSET 0
        MESHES 1
        MESH 0
        NAME triangle
        RADIUS 2
        GEOMETRYMODE 4
        LIGHTINGMODE 3
        TEXTUREMODE 3
        VERTICES 3
        0: 0 0 0 0
        1: 1 0 0 0
        2: 0 1 0 0
        TEXTURE VERTICES 0
        FACES 1
        0: -1 0 4 3 3 0 3 0,-1 1,-1 2,-1
        SECTION: HIERARCHYDEF
        HIERARCHY NODES 1
        """ + "\n" + RootRow + "\nEND\n";

    public ArchitectureAuthoringFormatTests()
    {
        Directory.CreateDirectory(_directory); _archive = Path.Combine(_directory, "original-models.goo");
        WriteModel(ModelText);
    }
    private void WriteModel(string text) => GobWriter.Build(_archive, [("3do/original.3do", Encoding.Latin1.GetBytes(text))]);
    private ExplicitArchiveResources Resources() => new([new(_archive, AgentWorkspace.Hash(File.ReadAllBytes(_archive)))]);
    private static JklDocument Scene()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        var template = new Template { Name = "original", Parent = "none" };
        template.Values["thingflags"] = "0x8"; template.Values["model3d"] = "original.3do";
        level.Templates.Add(template.Name, template);
        var thing = level.NewThing(); thing.Name = "architecture"; thing.Template = template.Name; thing.Sector = level.Sectors[0];
        level.Lights.Add(new Light { Position = Vec3.Zero, Range = 2, Intensity = 2, RgbIntensity = .5, Color = new(.25f, .5f, 1) });
        return new(level);
    }
    private ReplaceThingArchitectureCommand Prepare(JklDocument doc, string action, int? lightingIndex = null)
    {
        using var resources = Resources();
        return ArchitectureAuthoringService.Prepare(doc.Level, doc.Level.Things[0], resources, action, lightingIndex);
    }
    private JklDocument Baked()
    {
        var doc = Scene(); Prepare(doc, "bake").Apply(); return doc;
    }

    [Fact]
    public void BakeUsesIndependentChannelsFromCapturedModelAndSurvivesReopen()
    {
        var doc = Baked(); var thing = doc.Level.Things[0];
        Assert.Equal(new ArchitecturalLightVertex(2, .125, .25, .5), doc.Level.ArchitecturalLighting[0].Meshes[0].Vertices[0]);
        Assert.Equal(new ArchitecturalLightVertex(.5, .03125, .0625, .125), doc.Level.ArchitecturalLighting[0].Meshes[0].Vertices[1]);
        Assert.Equal(AgentWorkspace.Hash(Encoding.Latin1.GetBytes(ModelText)), thing.ArchLightingProvenance!.ModelSha256);
        string output = JklWriter.Build(doc); var reopened = JklParser.ParseDocument(output);
        Assert.Equal(thing.ArchLightingProvenance, reopened.Level.Things[0].ArchLightingProvenance);
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(reopened.Level, reopened.Level.Things[0]));
        Assert.Equal(output, JklWriter.Build(reopened));
        using var resources = Resources();
        var validation = ResourceValidatedExport.Prepare(reopened, resources).Validation;
        Assert.False(validation.HasErrors); Assert.False(Assert.Single(validation.Bindings).HistoricalBaselineVerified);
    }

    [Fact]
    public void PrecisePlacementAndLightingRemainCurrentAfterSerialization()
    {
        var doc = Scene(); var thing = doc.Level.Things[0];
        thing.Position = new(.12345678901234567, -.9876543210987654, .3333333333333333);
        thing.Pitch = 17.123456789012345; thing.Yaw = 23.23456789012345; thing.Roll = -7.34567890123456;
        doc.Level.Lights[0].Position = new(.23456789012345678, -.8765432109876543, .4444444444444444);
        doc.Level.Lights[0].Intensity = .8765432101234567; doc.Level.Lights[0].RgbIntensity = .12345678901234567;
        Prepare(doc, "bake").Apply(); var provenance = thing.ArchLightingProvenance;
        var reopened = JklParser.ParseDocument(JklWriter.Build(doc));
        Assert.Equal(provenance, reopened.Level.Things[0].ArchLightingProvenance);
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(reopened.Level, reopened.Level.Things[0]));
    }

    [Fact]
    public void BindClonesAnExistingAllocationWithoutClaimingABake()
    {
        var doc = Scene(); var allocation = new ArchitecturalLightObject(); var mesh = new ArchitecturalLightMesh();
        mesh.Vertices.AddRange(Enumerable.Repeat(new ArchitecturalLightVertex(-.25, .5, .75, 1), 3)); allocation.Meshes.Add(mesh);
        doc.Level.ArchitecturalLighting.Add(allocation);
        Prepare(doc, "bind", 0).Apply(); var thing = doc.Level.Things[0];
        Assert.Equal(1, thing.ArchLightingIndex); Assert.NotSame(allocation, doc.Level.ArchitecturalLighting[1]);
        Assert.Equal(allocation.Meshes[0].Vertices, doc.Level.ArchitecturalLighting[1].Meshes[0].Vertices);
        Assert.Equal("bound", thing.ArchLightingProvenance!.Origin); Assert.Empty(thing.ArchLightingProvenance.InputSha256);
        var reopened = JklParser.ParseDocument(JklWriter.Build(doc));
        Assert.Equal("bound", ArchitecturalLightingAuthoring.Status(reopened.Level, reopened.Level.Things[0]));
    }

    [Fact]
    public void BindRequiresExplicitCompatibleAllocationAndAnUnboundThing()
    {
        var doc = Scene(); Assert.Throws<ArgumentException>(() => Prepare(doc, "bind"));
        Assert.Throws<InvalidOperationException>(() => Prepare(doc, "bind", 0));
        Prepare(doc, "rebind").Apply(); Assert.Throws<ArgumentException>(() => Prepare(doc, "bind", 0));
    }

    [Fact]
    public void RebindResetsWithoutModifyingPriorSamplesOrClaimingCurrentBake()
    {
        var doc = Baked(); string old = ArchitecturalLightingAuthoring.SamplesDigest(doc.Level.ArchitecturalLighting[0]);
        Prepare(doc, "rebind").Apply(); var thing = doc.Level.Things[0];
        Assert.Equal(1, thing.ArchLightingIndex); Assert.Equal("reset", thing.ArchLightingProvenance!.Origin);
        Assert.All(doc.Level.ArchitecturalLighting[1].Meshes[0].Vertices, value => Assert.Equal(default, value));
        Assert.Equal(old, ArchitecturalLightingAuthoring.SamplesDigest(doc.Level.ArchitecturalLighting[0]));
    }

    [Fact]
    public void CurrentInputsBecomeStaleWithoutRewritingTheirRecordedDigest()
    {
        var doc = Baked(); var recorded = doc.Level.Things[0].ArchLightingProvenance;
        doc.Level.Lights[0].Intensity += .25;
        string output = JklWriter.Build(doc); var reopened = JklParser.ParseDocument(output);
        Assert.Equal(recorded, reopened.Level.Things[0].ArchLightingProvenance);
        Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(reopened.Level, reopened.Level.Things[0]));
        using var resources = Resources(); Assert.False(ResourceValidatedExport.Prepare(reopened, resources).Validation.HasErrors);
    }

    [Fact]
    public void ExplicitSampleOverrideKeepsPriorProvenanceAndReopensAsOverridden()
    {
        var doc = Baked(); var recorded = doc.Level.Things[0].ArchLightingProvenance;
        doc.Level.ArchitecturalLighting[0].Meshes[0].Vertices[0] = new(.123, .234, .345, .456);
        var reopened = JklParser.ParseDocument(JklWriter.Build(doc));
        Assert.Equal(recorded, reopened.Level.Things[0].ArchLightingProvenance);
        Assert.Equal("overridden", ArchitecturalLightingAuthoring.Status(reopened.Level, reopened.Level.Things[0]));
    }

    [Fact]
    public void ChangedRawModelRefusesExportAndBakeUntilExplicitReset()
    {
        var doc = Baked(); string before = JklWriter.Build(doc);
        string changed = "# whitespace-only historical-normalization control\n" + ModelText;
        Assert.Equal(ArchitectureModelFingerprint.Compute(ModelText), ArchitectureModelFingerprint.Compute(changed));
        WriteModel(changed);
        using (var resources = Resources())
        {
            Assert.Throws<AgentValidationException>(() => ResourceValidatedExport.Prepare(doc, resources));
            Assert.Throws<InvalidOperationException>(() => ArchitectureAuthoringService.Prepare(doc.Level, doc.Level.Things[0], resources, "bake"));
        }
        Assert.Equal(before, JklWriter.Build(doc));
        Prepare(doc, "rebind").Apply(); Prepare(doc, "bake").Apply();
        Assert.Equal(AgentWorkspace.Hash(Encoding.Latin1.GetBytes(changed)), doc.Level.Things[0].ArchLightingProvenance!.ModelSha256);
    }

    [Fact]
    public void LegacyWithoutAuthoredProvenanceDoesNotAcquireTheExtension()
    {
        var doc = Scene(); var allocation = new ArchitecturalLightObject(); var mesh = new ArchitecturalLightMesh();
        mesh.Vertices.AddRange(new ArchitecturalLightVertex[3]); allocation.Meshes.Add(mesh); doc.Level.ArchitecturalLighting.Add(allocation);
        doc.Level.Things[0].ArchLightingIndex = 0; doc.Level.Things[0].ArchLightingSourceModel = "original.3do";
        doc.Level.Things[0].ArchLightingSourceModelHash = ArchitectureModelFingerprint.Compute(ModelText)!;
        string output = JklWriter.Build(doc); Assert.DoesNotContain("SED_ARCH_AUTHORING", output);
        var reopened = JklParser.ParseDocument(output); Assert.Null(reopened.Level.Things[0].ArchLightingProvenance);
        Assert.Empty(reopened.Level.Things[0].ArchLightingSourceModelHash);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("section")]
    [InlineData("row")]
    [InlineData("checksum")]
    [InlineData("header")]
    public void MalformedOrUnsupportedAuthoringMetadataRefusesAtomically(string fault)
    {
        string text = JklWriter.Build(Baked()); int start = text.IndexOf("SECTION: SED_ARCH_AUTHORING", StringComparison.Ordinal);
        string extension = text[start..]; string[] lines = extension.Split('\n');
        string changed = fault switch
        {
            "version" => text[..start] + extension.Replace("VERSION 1", "VERSION 2"),
            "section" => text + "\n" + extension,
            "row" => text[..start] + extension.Replace("BINDINGS 1", "BINDINGS 2").Replace("\nEND", "\n" + lines[4] + "\nEND"),
            "checksum" => text[..start] + extension.Replace(lines[2], "BINDINGS_SHA256 " + new string('0', 64)),
            "header" => text.Replace("SECTION: SED_ARCH_AUTHORING", "SECTION: SED_ARCH_AUTHORING extra"),
            _ => throw new InvalidOperationException(),
        };
        if (fault == "version") Assert.Throws<NotSupportedException>(() => JklParser.ParseDocument(changed));
        else Assert.Throws<FormatException>(() => JklParser.ParseDocument(changed));
        var original = JklParser.ParseDocument(text);
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(original.Level, original.Level.Things[0]));
    }

    [Fact]
    public void OrdinarySampleTamperingCannotReuseAnUnchangedMetadataChecksum()
    {
        string text = JklWriter.Build(Baked());
        Assert.Contains("0: 2 0.125 0.25 0.5", text);
        string changed = text.Replace("0: 2 0.125 0.25 0.5", "0: 3 0.125 0.25 0.5");
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(changed));
    }

    [Fact]
    public void ValidatedExtensionAllowsGeometryEditingWhileRetainedSourceMutationRefuses()
    {
        var parsed = JklParser.ParseDocument(JklWriter.Build(Baked()));
        new CreateSectorCommand(parsed.Level, SectorFactory.CreateBox(parsed.Level, new(5, 0, 0), 1, "", -1)).Apply();
        var reopened = JklParser.ParseDocument(JklWriter.Build(parsed)); Assert.Equal(2, reopened.Level.Sectors.Count);
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(reopened.Level, reopened.Level.Things[0]));
        int metadata = Array.FindIndex(parsed.SourceLines, line => line.StartsWith("BINDINGS_SHA256", StringComparison.Ordinal));
        parsed.SourceLines[metadata] = "BINDINGS_SHA256 " + new string('0', 64);
        Assert.Throws<InvalidOperationException>(() => JklWriter.Build(parsed));
    }

    [Fact]
    public void NestedRestPoseAndThingPyrUseDoublePrecisionInDocumentedOrder()
    {
        string hierarchy = "HIERARCHY NODES 2\n0: 0 0x10 -1 -1 1 -1 1 1 0 0 0 90 0 0 0 0 parent\n"
            + "1: 0 0x20 0 0 -1 -1 0 0 0 0 0 0 0 0 0 0 child\nEND\n";
        WriteModel(ModelText[..ModelText.IndexOf("HIERARCHY NODES", StringComparison.Ordinal)] + hierarchy);
        var doc = Scene(); var thing = doc.Level.Things[0]; thing.Position = new(10, 0, 0); thing.Yaw = 90;
        // Local vertex(1,0,0) -> parent(1,1,0) -> thing(9,1,0).
        doc.Level.Lights[0].Position = new(9, 1, 0); Prepare(doc, "bake").Apply();
        var sample = doc.Level.ArchitecturalLighting[0].Meshes[0].Vertices[1];
        Assert.Equal(2, sample.Mono, 12); Assert.Equal(.125, sample.Red, 12);
    }

    [Fact]
    public void HierarchyRowLabelsRequireNumbersButDoNotRedefinePhysicalSlots()
    {
        WriteModel(ModelText.Replace(RootRow, "not-a-number:" + RootRow[2..]));
        var doc = Scene(); Assert.Throws<FormatException>(() => Prepare(doc, "bake"));
        Assert.Empty(doc.Level.ArchitecturalLighting);
        WriteModel(ModelText.Replace(RootRow, "42:" + RootRow[2..]).Replace("\n", "\r"));
        Prepare(doc, "bake").Apply();
        Assert.Equal(new ArchitecturalLightVertex(2, .125, .25, .5), doc.Level.ArchitecturalLighting[0].Meshes[0].Vertices[0]);
    }

    [Fact]
    public void OnlyCapturedBytesClaimRawIdentity()
    {
        byte[] bytes = Encoding.Latin1.GetBytes(ModelText);
        Assert.Equal(AgentWorkspace.Hash(bytes), ThreeDoParser.ParseBytes(bytes).RawSha256);
        Assert.Null(ThreeDoParser.Parse(ModelText).RawSha256);
        using var archive = GobArchive.Open(_archive);
        Assert.Equal(AgentWorkspace.Hash(bytes), new ModelLibrary(archive).Get("original.3do")!.RawSha256);
    }

    [Theory]
    [InlineData("pivot")]
    [InlineData("insert")]
    [InlineData("duplicate-instance")]
    [InlineData("parent-cycle")]
    [InlineData("missing-child")]
    [InlineData("flags")]
    public void UnsupportedTransformsRefuseBeforeAnyBindingOrSampleMutation(string fault)
    {
        string row = RootRow;
        string model = fault switch
        {
            "pivot" => ModelText.Replace(row, "0: 0 0x10 0 -1 -1 -1 0 0 0 0 0 0 0 1 0 0 root"),
            "insert" => ModelText.Replace("INSERT OFFSET 0 0 0", "INSERT OFFSET 1 0 0"),
            "duplicate-instance" => ModelText.Replace("HIERARCHY NODES 1", "HIERARCHY NODES 2").Replace(row,
                "0: 0 0x10 0 -1 1 -1 1 0 0 0 0 0 0 0 0 0 root\n1: 0 0x10 0 0 -1 -1 0 0 0 0 0 0 0 0 0 0 repeated"),
            "parent-cycle" => ModelText.Replace(row, "0: 0 0x10 0 0 -1 -1 0 0 0 0 0 0 0 0 0 0 root"),
            "missing-child" => ModelText.Replace(row, "0: 0 0x10 0 -1 -1 -1 1 0 0 0 0 0 0 0 0 0 root"),
            "flags" => ModelText.Replace(row, row.Replace("0x0", "0x1")),
            _ => throw new InvalidOperationException(),
        };
        WriteModel(model); var doc = Scene(); string before = JklWriter.Build(doc);
        Assert.Throws<InvalidOperationException>(() => Prepare(doc, "bake")); Assert.Equal(before, JklWriter.Build(doc));
        Assert.Empty(doc.Level.ArchitecturalLighting); Assert.Null(doc.Level.Things[0].ArchLightingProvenance);
    }

    [Fact]
    public void BindSupportsAllLodSlotsButNewSamplingRefusesUnknownLodCorrespondence()
    {
        WriteModel(ArchitectureTopologyFixture.Build([[3], [2]])); var doc = Scene();
        var allocation = new ArchitecturalLightObject(); var mesh = new ArchitecturalLightMesh();
        mesh.Vertices.AddRange(new ArchitecturalLightVertex[3]); allocation.Meshes.Add(mesh); doc.Level.ArchitecturalLighting.Add(allocation);
        Prepare(doc, "bind", 0).Apply(); Assert.Equal("bound", doc.Level.Things[0].ArchLightingProvenance!.Origin);
        string before = JklWriter.Build(doc);
        Assert.Throws<InvalidOperationException>(() => Prepare(doc, "bake")); Assert.Equal(before, JklWriter.Build(doc));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

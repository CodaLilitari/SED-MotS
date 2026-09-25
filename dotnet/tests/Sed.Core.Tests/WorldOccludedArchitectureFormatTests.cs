using System.Text;
using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

public sealed class WorldOccludedArchitectureFormatTests : IDisposable
{
    private const string Profile = ArchitecturalLightingAuthoring.WorldOccludedBakeProfile;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-world-arch-" + Guid.NewGuid().ToString("N"));
    private string Archive => Path.Combine(_directory, "original-fixture.goo");
    private const string Model = """
        # Original synthetic triangle for authoring policy tests.
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
        NAME original
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
        0: 0 0x10 0 -1 -1 -1 0 0 0 0 0 0 0 0 0 0 original
        END
        """;

    public WorldOccludedArchitectureFormatTests()
    {
        Directory.CreateDirectory(_directory); WriteModel(Model);
    }
    private void WriteModel(string text) => GobWriter.Build(Archive, [("3do/original.3do", Encoding.Latin1.GetBytes(text))]);
    private ExplicitArchiveResources Resources() => new([new(Archive, AgentWorkspace.Hash(File.ReadAllBytes(Archive)))]);
    private ReplaceThingArchitectureCommand Prepare(JklDocument document, string action = "bake", string profile = Profile)
    {
        using var resources = Resources();
        return ArchitectureAuthoringService.Prepare(document.Level, document.Level.Things[0], resources, action, profile: profile);
    }
    private static (JklDocument Document, Surface Wall) Scene()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        var template = new Template { Name = "original", Parent = "none" };
        template.Values["thingflags"] = "0x8"; template.Values["model3d"] = "original.3do";
        level.Templates.Add(template.Name, template);
        var thing = level.NewThing(); thing.Template = template.Name; thing.Name = "architecture"; thing.Sector = level.Sectors[0];
        level.Lights.Add(new Light { Position = Vec3.Zero, Range = 4, Intensity = .8, RgbIntensity = .4, Color = new(1, .5f, .25f) });
        var sector = level.Sectors[0]; var wall = sector.NewSurface();
        foreach (var p in new[] { new Vec3(.5, -.75, -.75), new(.5, .75, -.75), new(.5, .75, .75), new(.5, -.75, .75) })
            wall.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(p) });
        return (new JklDocument(level), wall);
    }

    [Fact]
    public void ExplicitProfileUsesCapturedModelAndPreservesProvenanceAcrossReadback()
    {
        var (doc, _) = Scene(); Prepare(doc).Apply();
        var thing = doc.Level.Things[0]; var samples = doc.Level.ArchitecturalLighting[0].Meshes[0].Vertices;
        Assert.Equal(new ArchitecturalLightVertex(.8, .4, .2, .1), samples[0]);
        Assert.Equal(default, samples[1]); // Source-to-vertex ray crosses the interior wall.
        Assert.True(samples[2].Mono > 0);
        Assert.Equal(Profile, thing.ArchLightingProvenance!.Origin);
        Assert.Equal(AgentWorkspace.Hash(Encoding.Latin1.GetBytes(Model)), thing.ArchLightingProvenance.ModelSha256);
        string text = JklWriter.Build(doc); var reopened = JklParser.ParseDocument(text);
        Assert.Equal(thing.ArchLightingProvenance, reopened.Level.Things[0].ArchLightingProvenance);
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(reopened.Level, reopened.Level.Things[0]));
        Assert.Equal(samples, reopened.Level.ArchitecturalLighting[0].Meshes[0].Vertices);
        Assert.Equal(text, JklWriter.Build(reopened));
    }

    [Fact]
    public void WorldGeometryChangeRemainsStaleAfterReadbackAndDigestIsNotRewritten()
    {
        var (doc, wall) = Scene(); Prepare(doc).Apply();
        var recorded = doc.Level.Things[0].ArchLightingProvenance;
        foreach (var c in wall.Corners) c.Vertex.Position += new Vec3(.25, 0, 0);
        var reopened = JklParser.ParseDocument(JklWriter.Build(doc));
        Assert.Equal(recorded, reopened.Level.Things[0].ArchLightingProvenance);
        Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(reopened.Level, reopened.Level.Things[0]));
    }

    [Fact]
    public void ModelLightingModesDoNotChangeStoredSamplesOrCreateModelShadows()
    {
        var (first, _) = Scene(); Prepare(first).Apply();
        string modified = Model.Replace("LIGHTINGMODE 3", "LIGHTINGMODE 0")
            .Replace("0: -1 0 4 3 3 0 3", "0: -1 2 4 0 3 0 3");
        WriteModel(modified);
        var (second, _) = Scene(); Prepare(second).Apply();
        Assert.Equal(first.Level.ArchitecturalLighting[0].Meshes[0].Vertices,
            second.Level.ArchitecturalLighting[0].Meshes[0].Vertices);
        Assert.NotEqual(first.Level.Things[0].ArchLightingProvenance!.ModelSha256,
            second.Level.Things[0].ArchLightingProvenance!.ModelSha256);
        // Fullbright/face translucency remain renderer behavior, not this
        // vertex-sampling recipe. No pixels or gameplay were evaluated here.
    }

    [Fact]
    public void OmittedProfileStillSelectsOriginalUnoccludedRecipe()
    {
        var (doc, _) = Scene();
        using var resources = Resources();
        ArchitectureAuthoringService.Prepare(doc.Level, doc.Level.Things[0], resources, "bake").Apply();
        Assert.Equal(ArchitecturalLightingAuthoring.BakeProfile, doc.Level.Things[0].ArchLightingProvenance!.Origin);
        Assert.True(doc.Level.ArchitecturalLighting[0].Meshes[0].Vertices[1].Mono > 0);
    }

    [Fact]
    public void UnsupportedProfileOrNonBakeUseRefusesBeforeMutation()
    {
        var (doc, _) = Scene();
        Assert.Throws<ArgumentException>(() => Prepare(doc, profile: "invented"));
        Assert.Throws<ArgumentException>(() => Prepare(doc, action: "rebind"));
        Assert.Empty(doc.Level.ArchitecturalLighting); Assert.Null(doc.Level.Things[0].ArchLightingProvenance);
    }

    [Fact]
    public void NewOriginStillRequiresInputDigestAndRejectsUnknownOrigins()
    {
        var (doc, _) = Scene(); Prepare(doc).Apply();
        string text = JklWriter.Build(doc), input = doc.Level.Things[0].ArchLightingProvenance!.InputSha256;
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(text.Replace(input, "none")));
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(text.Replace(Profile, "invented-profile")));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

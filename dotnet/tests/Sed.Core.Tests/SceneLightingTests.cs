using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.ThreeDo;
using Sed.Rendering;

namespace Sed.Core.Tests;

public sealed class SceneLightingTests
{
    [Fact]
    public void AdjoinStateSplitsOtherwiseIdenticalMaterialBatches()
    {
        var (level, sector, surface) = World();
        var second = sector.NewSurface();
        second.Material = surface.Material;
        second.SurfFlags = surface.SurfFlags;
        foreach (var corner in surface.Corners) second.Corners.Add(corner);
        second.Adjoin = surface;
        var scene = Surfaces(level);
        Assert.Equal(2, scene.Submeshes.Count);
        Assert.Single(scene.Submeshes, s => s.Adjoined);
        Assert.Single(scene.Submeshes, s => !s.Adjoined);
    }

    private const long MotsRgb = 0x1000000;

    private static (Level Level, Sector Sector, Surface Surface) World(ProjectType kind = ProjectType.MysteriesOfTheSith)
    {
        var level = new Level { Kind = kind };
        var sector = level.NewSector();
        var surface = sector.NewSurface();
        surface.Material = "shared.mat";
        surface.SurfFlags = MotsRgb;
        foreach (var point in new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(0, 1, 0) })
            surface.Corners.Add(new Surface.Corner
            {
                Vertex = sector.AddVertex(point), MonoIntensity = .2f, Intensity = new ColorF(.1f, .4f, .9f)
            });
        return (level, sector, surface);
    }

    private static ThreeDoModel Model(int meshes = 1)
    {
        var model = new ThreeDoModel();
        model.Materials.Add("shared.mat");
        for (int m = 0; m < meshes; m++)
        {
            var mesh = new Mesh3do();
            mesh.Vertices.AddRange(new[] { new Vec3(m * 2, 0, 0), new Vec3(m * 2 + 1, 0, 0), new Vec3(m * 2, 1, 0) });
            mesh.VertexIntensities.AddRange(new[] { .11f + m * .3f, .22f + m * .3f, .33f + m * .3f });
            var face = new Face3do { Material = 0 };
            face.VertexIndices.AddRange(new[] { 2, 0, 1 }); // Deliberately not vertex-table order.
            mesh.Faces.Add(face);
            model.Meshes.Add(mesh);
        }
        model.ArchitectureTopology = new(2.2, [new(model.Meshes.Select(m => m.Vertices.Count).ToArray())], null);
        return model;
    }

    private static Thing Place(Level level, Sector? sector = null, int binding = -1)
    {
        var thing = level.NewThing();
        thing.Sector = sector;
        thing.Values["model3d"] = "synthetic.3do";
        if (binding >= 0) thing.Values["thingflags"] = "0x8";
        thing.ArchLightingIndex = binding;
        return thing;
    }

    private static ArchitecturalLightObject Architecture(int meshes = 1, double shift = 0)
    {
        var obj = new ArchitecturalLightObject();
        for (int m = 0; m < meshes; m++)
        {
            var mesh = new ArchitecturalLightMesh();
            for (int v = 0; v < 3; v++)
                mesh.Vertices.Add(new ArchitecturalLightVertex(.05 + shift + m * .1 + v * .01,
                    .1 + shift + m * .2 + v * .02, .3 + shift + m * .1 + v * .03, .7 - shift - m * .1 - v * .04));
            obj.Meshes.Add(mesh);
        }
        return obj;
    }

    [Fact]
    public void OrdinaryModelsUseMonoAndTemplateFlagsAreReplacedByInstanceFlags()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var parent = new Template { Name = "base" }; parent.Values["thingflags"] = "8";
        level.Templates[parent.Name] = parent;
        level.Templates["child"] = new Template { Name = "child", Parent = "base" };
        var thing = Place(level); thing.Template = "child";
        Assert.False(Assert.Single(Things(level, Model()).Submeshes).UseMonochromeLighting);
        thing.Values["thingflags"] = "0x0";
        Assert.True(Assert.Single(Things(level, Model()).Submeshes).UseMonochromeLighting);
        thing.ArchLightingIndex = 0; level.ArchitecturalLighting.Add(Architecture());
        var rejectedBinding = Things(level, Model());
        Assert.Contains(rejectedBinding.Diagnostics, s => s.Contains("lacks resolved thingflags"));
        Light(rejectedBinding.Mesh.Vertices[0], .33f, .33f, .33f, .33f);
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, 0)]
    [InlineData(ProjectType.MysteriesOfTheSith, 0)]
    [InlineData(ProjectType.InfernalMachine, 3)]
    public void TextureClampBitsAreStoredButOnlyAppliedForInfernalMachine(ProjectType kind, int expected)
    {
        var (level, _, surface) = World(kind);
        surface.FaceFlags = FaceFlags.TexClampX | FaceFlags.TexClampY;
        Assert.Equal(expected, Assert.Single(Surfaces(level).Submeshes).ClampMode);
        Assert.Equal(FaceFlags.TexClampX | FaceFlags.TexClampY, surface.FaceFlags);
    }

    [Fact]
    public void MissingNamedModelAndNonfiniteLightCannotBeSilentlyCertified()
    {
        var (level, _, surface) = World();
        Place(level);
        var assembler = new SceneAssembler();
        Assert.Single(assembler.AddThings(level, _ => null));
        Assert.Contains(assembler.Build().Diagnostics, s => s.Contains("named model"));
        surface.Corners[0].MonoIntensity = float.NaN;
        Assert.Throws<FormatException>(() => Surfaces(level));
    }

    private static RenderScene Surfaces(Level level)
    {
        var assembler = new SceneAssembler();
        assembler.AddLevel(level);
        return assembler.Build();
    }

    private static RenderScene Things(Level level, ThreeDoModel model)
    {
        var assembler = new SceneAssembler();
        Assert.Empty(assembler.AddThings(level, _ => model));
        return assembler.Build();
    }

    private static void Light(MeshVertex actual, float mono, float r, float g, float b)
    {
        Assert.Equal(mono, actual.Mono, 5);
        Assert.Equal(r, actual.R, 5);
        Assert.Equal(g, actual.G, 5);
        Assert.Equal(b, actual.B, 5);
    }

    [Theory]
    [InlineData(ProjectType.MysteriesOfTheSith, RenderLightingMode.Rgb)]
    [InlineData(ProjectType.JediKnight, RenderLightingMode.Mono)]
    public void SceneGameSelectsDisplayChannelWithoutLosingTheAlternateChannel(ProjectType kind, RenderLightingMode mode)
    {
        var (level, _, surface) = World(kind);
        surface.Corners[1].MonoIntensity = .8f;
        var scene = Surfaces(level);
        Assert.Equal(mode, scene.LightingMode);
        Light(scene.Mesh.Vertices[0], .2f, .1f, .4f, .9f);
        Light(scene.Mesh.Vertices[1], .8f, .1f, .4f, .9f);
        Assert.Equal(.8f, surface.Corners[1].MonoIntensity);
    }

    [Fact]
    public void NewlyAuthoredMotSRgbIsVisibleBeforeTheEngineDerivesItsLoadTimeFlag()
    {
        var (level, _, surface) = World();
        surface.SurfFlags = 0;
        var scene = Surfaces(level);
        Assert.All(scene.Mesh.Vertices, vertex => Light(vertex, .2f, .1f, .4f, .9f));
        Assert.Equal(new ColorF(.1f, .4f, .9f), surface.Corners[0].Intensity);
        Assert.Equal(0, surface.SurfFlags);
    }

    [Fact]
    public void ImplicitMonoUsesRedForNewlyAuthoredCorners()
    {
        var (level, _, surface) = World();
        surface.Corners[0].MonoIntensity = null;
        Light(Surfaces(level).Mesh.Vertices[0], .1f, .1f, .4f, .9f);
    }

    [Fact]
    public void WorldAddsFaceExtraThenUsesSectorExtraFloorWithoutSectorAmbient()
    {
        var (level, sector, surface) = World();
        sector.Ambient = new ColorF(.95f, .95f, .95f);
        sector.ExtraLight = new ColorF(.3f, .3f, .3f);
        surface.ExtraLightIntensity = .15f;
        Assert.All(Surfaces(level).Mesh.Vertices, vertex => Light(vertex, .35f, .3f, .55f, 1));
    }

    [Fact]
    public void BaseIsClampedBeforeNegativeFaceExtraIsApplied()
    {
        var (level, _, surface) = World();
        surface.ExtraLightIntensity = -.6f;
        foreach (var corner in surface.Corners)
        {
            corner.MonoIntensity = 1.4f;
            corner.Intensity = new ColorF(1.4f, .5f, -.2f);
        }
        Assert.All(Surfaces(level).Mesh.Vertices, vertex => Light(vertex, .4f, .4f, 0, 0));
    }

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(1, .3f)]
    [InlineData(2, .3f)]
    public void WorldFullbrightAndNonGouraudModesHaveExplicitStaticPreview(int mode, float expected)
    {
        var (level, sector, surface) = World();
        surface.Light = mode;
        surface.ExtraLightIntensity = .15f;
        sector.ExtraLight = new ColorF(.3f, .3f, .3f);
        Assert.All(Surfaces(level).Mesh.Vertices, vertex => Light(vertex, expected, expected, expected, expected));
    }

    [Theory]
    [InlineData(Surface.SfSkyCeiling, 1)]
    [InlineData(Surface.SfSkyHorizon, 2)]
    public void SkyAlwaysUsesFullLightAndPreservesSkyMode(long flag, int skyMode)
    {
        var (level, _, surface) = World();
        surface.SurfFlags |= flag;
        surface.Light = 1;
        surface.ExtraLightIntensity = -1;
        var scene = Surfaces(level);
        Assert.Equal(skyMode, Assert.Single(scene.Submeshes).SkyMode);
        Assert.All(scene.Mesh.Vertices, vertex => Light(vertex, 1, 1, 1, 1));
    }

    [Fact]
    public void SameMaterialWithDifferentSectorColormapsFormsSeparateBatches()
    {
        var (level, sector, _) = World();
        level.ColorMaps.Add("DEFAULT.CMP");
        var other = level.NewSector();
        other.ColorMap = "second.cmp";
        var face = other.NewSurface();
        face.Material = "SHARED.MAT";
        foreach (var vertex in sector.Vertices)
            face.Corners.Add(new Surface.Corner { Vertex = other.AddVertex(vertex.Position + new Vec3(2, 0, 0)) });
        var scene = Surfaces(level);
        Assert.Equal(2, scene.Submeshes.Count);
        Assert.Equal(new[] { "default.cmp", "second.cmp" }, scene.Submeshes.Select(s => s.Colormap));
        Assert.All(scene.Submeshes, sub => Assert.Equal("shared.mat", sub.Material));
    }

    [Fact]
    public void HiddenWorldAndModelGeometryAreOmitted()
    {
        var (level, sector, surface) = World();
        surface.Geo = 0;
        Assert.Empty(Surfaces(level).Mesh.Vertices);
        Place(level, sector);
        var model = Model(3);
        model.Meshes[0].GeometryMode = 0;
        model.Meshes[1].Faces[0].Geo = 0;
        var scene = Things(level, model);
        Assert.Equal(3, scene.Mesh.Vertices.Count);
        Assert.All(scene.Mesh.Vertices, vertex => Assert.InRange(vertex.Px, 4, 5));
    }

    [Fact]
    public void ModelScalarBaseLightUsesActualVertexIndexAndIncludesSectorAmbientFloor()
    {
        var (level, sector, _) = World();
        sector.Ambient = new ColorF(.2f, .2f, .2f);
        sector.ExtraLight = new ColorF(.15f, .15f, .15f);
        Place(level, sector);
        var model = Model();
        model.Meshes[0].Faces[0].ExtraLight = .1f;
        var scene = Things(level, model);
        Light(scene.Mesh.Vertices[0], .43f, .43f, .43f, .43f); // index2
        Light(scene.Mesh.Vertices[1], .35f, .35f, .35f, .35f); // index0
        Light(scene.Mesh.Vertices[2], .35f, .35f, .35f, .35f); // index1
        Assert.Equal(new[] { .11f, .22f, .33f }, model.Meshes[0].VertexIntensities);
    }

    [Theory]
    [InlineData(3, 0, 1f)]
    [InlineData(3, 1, .2f)]
    [InlineData(1, 3, .2f)]
    [InlineData(6, 0, 1f)]
    public void ModelFaceAndMeshLightModesBothLimitThePreview(int meshMode, int faceMode, float expected)
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        Place(level);
        var model = Model();
        model.Meshes[0].LightingMode = meshMode;
        model.Meshes[0].Faces[0].Light = faceMode;
        model.Meshes[0].Faces[0].ExtraLight = .2f;
        Assert.All(Things(level, model).Mesh.Vertices, vertex => Light(vertex, expected, expected, expected, expected));
    }

    [Fact]
    public void ArchitectureIndexZeroPreservesDistinctMeshesVerticesAndInstancesWithoutMutatingCachedModel()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.ArchitecturalLighting.Add(Architecture(2));
        level.ArchitecturalLighting.Add(Architecture(2, .1));
        Place(level, binding: 0);
        Place(level, binding: 1).Position = new Vec3(10, 0, 0);
        var model = Model(2);
        var scene = Things(level, model);
        Assert.Empty(scene.Diagnostics);
        Assert.Equal(12, scene.Mesh.Vertices.Count);
        int output = 0;
        foreach (var architecture in level.ArchitecturalLighting)
            foreach (var mesh in architecture.Meshes)
                foreach (int index in new[] { 2, 0, 1 })
                {
                    var value = mesh.Vertices[index];
                    Light(scene.Mesh.Vertices[output++], (float)value.Mono, (float)value.Red, (float)value.Green, (float)value.Blue);
                }
        Assert.Equal(new[] { .11f, .22f, .33f }, model.Meshes[0].VertexIntensities);
        Assert.Equal(.05, level.ArchitecturalLighting[0].Meshes[0].Vertices[0].Mono);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("mesh-count")]
    [InlineData("vertex-count")]
    [InlineData("model-name")]
    [InlineData("model-hash")]
    public void InvalidArchitecturalBindingUsesBaseLightWithDiagnostic(string mismatch)
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var architecture = Architecture();
        level.ArchitecturalLighting.Add(architecture);
        var thing = Place(level, binding: mismatch == "index" ? 9 : 0);
        if (mismatch == "mesh-count") architecture.Meshes.Add(new ArchitecturalLightMesh());
        if (mismatch == "vertex-count") architecture.Meshes[0].Vertices.RemoveAt(2);
        if (mismatch == "model-name") thing.ArchLightingSourceModel = "old.3do";
        if (mismatch == "model-hash") thing.ArchLightingSourceModelHash = new string('a', 64);
        var scene = Things(level, Model());
        Assert.Contains(scene.Diagnostics, diagnostic => diagnostic.Contains("Thing 0") && diagnostic.Contains("binding"));
        Light(scene.Mesh.Vertices[0], .33f, .33f, .33f, .33f);
        Assert.Equal(mismatch == "index" ? 9 : 0, thing.ArchLightingIndex);
    }

    [Fact]
    public void ArchitecturalMeshSelectionFollowsHierarchyMeshIndexNotNodeOrder()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var architecture = Architecture(2);
        level.ArchitecturalLighting.Add(architecture);
        Place(level, binding: 0);
        var model = Model(2);
        model.Nodes.Add(new HierarchyNode { Mesh = 1, Parent = -1 });
        model.Nodes.Add(new HierarchyNode { Mesh = 0, Parent = 0 });
        var scene = Things(level, model);
        Assert.Equal(6, scene.Mesh.Vertices.Count);
        var first = architecture.Meshes[1].Vertices[2];
        var second = architecture.Meshes[0].Vertices[2];
        Light(scene.Mesh.Vertices[0], (float)first.Mono, (float)first.Red, (float)first.Green, (float)first.Blue);
        Light(scene.Mesh.Vertices[3], (float)second.Mono, (float)second.Red, (float)second.Green, (float)second.Blue);
    }

    [Fact]
    public void TriangleBudgetRejectsOverrunAndHierarchyCycleRejectsBeforeRecursingForever()
    {
        var (level, _, _) = World();
        var assembler = new SceneAssembler(maxTriangles: 1);
        assembler.AddLevel(level);
        Assert.Throws<InvalidOperationException>(() => assembler.AddLevel(level));
        var model = Model();
        model.Nodes.Add(new HierarchyNode { Mesh = 0, Parent = 1 });
        model.Nodes.Add(new HierarchyNode { Mesh = 0, Parent = 0 });
        Assert.Throws<FormatException>(() => new SceneAssembler().AddModel(model, Mat4.Identity));
    }
}

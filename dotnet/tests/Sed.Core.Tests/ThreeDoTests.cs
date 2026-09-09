using Sed.Core.Model;
using Sed.Formats.ThreeDo;
using Xunit;

namespace Sed.Core.Tests;

public class ThreeDoTests
{
    [Fact]
    public void ACompleteFirstMeshDoesNotHideATruncatedFinalMesh()
    {
        string prefix = SampleModel[..SampleModel.IndexOf("FACE NORMALS 1", StringComparison.Ordinal)]
            .Replace("MESHES 1", "MESHES 2");
        string lastMesh = "MESH 1\nNAME empty\nVERTICES 0\nTEXTURE VERTICES 0\n";
        Assert.Throws<FormatException>(() => ThreeDoParser.Parse(prefix + lastMesh + "END\n"));
        Assert.Throws<FormatException>(() => ThreeDoParser.Parse(prefix + lastMesh));
        Assert.Equal(2, ThreeDoParser.Parse(prefix + lastMesh + "FACES 0\nEND\n").Meshes.Count);
    }

    [Theory]
    [InlineData("TEXTURE\tVERTICES\t3")]
    [InlineData("TEXTURE  VERTICES  3")]
    public void TextureHeaderAcceptsEngineCompatibleWhitespace(string header)
    {
        var model = ThreeDoParser.Parse(SampleModel.Replace("TEXTURE VERTICES 3", header));
        Assert.Equal(3, Assert.Single(model.Meshes).Uvs.Count);
        Assert.Equal(new[] { 0, 1, 2 }, Assert.Single(model.Meshes[0].Faces).UvIndices);
    }

    [Theory]
    [InlineData("VERTICES 3", "VERTICES 2147483647")]
    [InlineData("TEXTURE VERTICES 3", "TEXTURE VERTICES 2147483647")]
    [InlineData("FACES 1", "FACES 2147483647")]
    [InlineData("MESHES 1", "MESHES -1")]
    public void DeclaredCountsCannotAllocateBeyondDecodedBudget(string before, string after)
        => Assert.Throws<FormatException>(() => ThreeDoParser.Parse(SampleModel.Replace(before, after)));

    [Theory]
    [InlineData("VERTICES 3")][InlineData("TEXTURE VERTICES 3")][InlineData("FACES 1")]
    public void TruncatedDeclaredTablesAreRefusedImmediately(string header)
    {
        int end = SampleModel.IndexOf(header, StringComparison.Ordinal) + header.Length;
        Assert.Throws<FormatException>(() => ThreeDoParser.Parse(SampleModel[..end]));
    }

    private const string SampleModel = """
        SECTION: HEADER
        3DO 2.1
        END

        SECTION: MODELRESOURCE
        MATERIALS 1
        0: wall.mat
        END

        SECTION: GEOMETRYDEF
        RADIUS 1.0
        INSERT OFFSET 0.0 0.0 0.0
        GEOSETS 1
        GEOSET 0
        MESHES 1
        MESH 0
        NAME tri
        GEOMETRYMODE 4
        LIGHTINGMODE 3
        TEXTUREMODE 3
        VERTICES 3
        0: 0.0 0.0 0.0
        1: 1.0 0.0 0.0
        2: 0.0 1.0 0.0
        TEXTURE VERTICES 3
        0: 0.0 0.0
        1: 1.0 0.0
        2: 0.0 1.0
        VERTEX NORMALS 3
        0: 0.0 0.0 1.0
        1: 0.0 0.0 1.0
        2: 0.0 0.0 1.0
        FACES 1
        0: 0 0x0 3 0 0 0.5 3  0,0 1,1 2,2
        FACE NORMALS 1
        0: 0.0 0.0 1.0
        END

        SECTION: HIERARCHYDEF
        HIERARCHY NODES 1
        0: 0x0 0x00010 0 -1 -1 -1 0 0.0 0.0 0.0 0.0 0.0 0.0 0.0 0.0 0.0 root
        END
        """;

    [Fact]
    public void Parses_MeshGeometryMaterialsAndHierarchy()
    {
        var model = ThreeDoParser.Parse(SampleModel);

        Assert.Equal(2.1, model.Version, 3);
        Assert.Single(model.Materials);
        Assert.Equal("wall.mat", model.Materials[0]);

        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(3, mesh.Vertices.Count);
        Assert.Equal(3, mesh.Uvs.Count);
        Assert.Equal(new Sed.Core.Math.Vec3(1, 0, 0), mesh.Vertices[1]);

        var face = Assert.Single(mesh.Faces);
        Assert.Equal(0, face.Material);
        Assert.Equal(3, face.VertexIndices.Count);
        Assert.Equal(new[] { 0, 1, 2 }, face.VertexIndices);
        Assert.Equal(new[] { 0, 1, 2 }, face.UvIndices);
        Assert.Equal(3, face.Geo); Assert.Equal(0, face.Light); Assert.Equal(0, face.Tex);
        Assert.Equal(0.5f, face.ExtraLight); Assert.Equal(0, face.FaceFlags);
        Assert.Equal(4, mesh.GeometryMode); Assert.Equal(3, mesh.LightingMode); Assert.Equal(3, mesh.TextureMode);
        Assert.Equal(new[] { 0f, 0f, 0f }, mesh.VertexIntensities);
        Assert.Equal(3, mesh.VertexNormals.Count);
        Assert.All(mesh.VertexNormals, n => Assert.Equal(new Sed.Core.Math.Vec3(0, 0, 1), n));

        var node = Assert.Single(model.Nodes);
        Assert.Equal(0, node.Mesh);
        Assert.Equal(-1, node.Parent);
    }

    [Fact]
    public void DistinctModelBaseLightsAndMeshFaceModesSurviveSave()
    {
        var text = SampleModel.ReplaceLineEndings("\n").Replace("0: 0.0 0.0 0.0\n", "0: 0.0 0.0 0.0 0.125\n")
            .Replace("1: 1.0 0.0 0.0\n", "1: 1.0 0.0 0.0 0.5\n")
            .Replace("2: 0.0 1.0 0.0\n", "2: 0.0 1.0 0.0 0.875\n")
            .Replace("LIGHTINGMODE 3", "LIGHTINGMODE 2").Replace("0: 0 0x0 3 0 0 0.5", "0: 0 0x12 4 3 1 -0.25");
        var model = ThreeDoParser.Parse(text);
        var reparsed = ThreeDoParser.Parse(ThreeDoWriter.Build(model));
        Assert.Equal(new[] { 0.125f, 0.5f, 0.875f }, reparsed.Meshes[0].VertexIntensities);
        Assert.Equal(2, reparsed.Meshes[0].LightingMode);
        var face = reparsed.Meshes[0].Faces[0];
        Assert.Equal(0x12, face.FaceFlags); Assert.Equal(4, face.Geo);
        Assert.Equal(3, face.Light); Assert.Equal(1, face.Tex); Assert.Equal(-0.25f, face.ExtraLight);
    }

    [Fact]
    public void NonfiniteModelBaseLightingIsRefused()
    {
        Assert.Throws<FormatException>(() => ThreeDoParser.Parse(SampleModel.ReplaceLineEndings("\n").Replace("0: 0.0 0.0 0.0\n", "0: 0.0 0.0 0.0 NaN\n")));
    }
}

public class TemplateResolutionTests
{
    [Fact]
    public void GetThingModel_FollowsParentChain()
    {
        var level = new Level();
        level.Templates["base"] = new Template { Name = "base", Values = { ["model3d"] = "ky.3do" } };
        level.Templates["player"] = new Template { Name = "player", Parent = "base" };

        var thing = new Thing { Template = "player" };
        Assert.Equal("ky.3do", level.GetThingModel(thing));
    }

    [Fact]
    public void GetThingModel_ThingParamOverridesTemplate()
    {
        var level = new Level();
        level.Templates["crate"] = new Template { Name = "crate", Values = { ["model3d"] = "crate.3do" } };
        var thing = new Thing { Template = "crate" };
        thing.Values["model3d"] = "special.3do";
        Assert.Equal("special.3do", level.GetThingModel(thing));
    }

    [Fact]
    public void GetThingModel_EmptyWhenUnknown()
    {
        var level = new Level();
        Assert.Equal("", level.GetThingModel(new Thing { Template = "nope" }));
    }
}

using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;
using Xunit;

namespace Sed.Core.Tests;

public sealed class AgentMaterialTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-agent-material-" + Guid.NewGuid().ToString("N"));
    public AgentMaterialTests() => Directory.CreateDirectory(_directory);
    private string Input(string game = "mots")
    {
        string input = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".jkl");
        AgentWorkspace.Create(input, game); return input;
    }
    private static PreparedAgentEdit Plan(string input, string operations) => AgentWorkspace.Plan(input,
        "{\"schemaVersion\":1,\"inputSha256\":\"" + AgentWorkspace.Load(input).Hash + "\",\"operations\":" + operations + "}");

    [Theory]
    [InlineData("jk")]
    [InlineData("mots")]
    public void FreshScaffoldCanRegisterAssignAndFitResourcesWithoutGameFiles(string game)
    {
        string input = Input(game); byte[] before = File.ReadAllBytes(input);
        var plan = Plan(input, """
            [{"op":"registerMaterial","name":"wall01.mat"},
             {"op":"registerColormap","name":"dflt.cmp"},
             {"op":"setSectorColormap","sector":0,"colormapIndex":0},
             {"op":"setSurfaceMaterial","sector":0,"surface":0,"materialIndex":0},
             {"op":"fitSurfaceTexture","sector":0,"surface":0,"width":128,"height":32}]
            """);
        var doc = JklParser.ParseDocument(plan.OutputText); var surface = doc.Level.Sectors[0].Surfaces[0];
        Assert.Equal(new[] { "wall01.mat" }, doc.Materials);
        Assert.Equal(new[] { "dflt.cmp" }, doc.Level.ColorMaps);
        Assert.Equal("dflt.cmp", doc.Level.Sectors[0].ColorMap);
        Assert.Equal(0, surface.MaterialIndex); Assert.Equal("wall01.mat", surface.Material);
        Assert.Equal(128, surface.Corners.Max(c => c.Uv.U)); Assert.Equal(32, surface.Corners.Max(c => c.Uv.V));
        Assert.Equal(0, surface.Corners.Min(c => c.Uv.U)); Assert.Equal(0, surface.Corners.Min(c => c.Uv.V));
        var report = JsonSerializer.SerializeToElement(plan.Report, AgentWorkspace.JsonOptions);
        Assert.Equal(0, report.GetProperty("changes")[0].GetProperty("serializedFinal").GetProperty("target").GetProperty("index").GetInt32());
        Assert.Equal(before, File.ReadAllBytes(input)); Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ExplicitUvPreservesDistinctMotsChannelsAndGeometryThroughSave()
    {
        string input = Input(); var doc = AgentWorkspace.Load(input).Document;
        var surface = doc.Level.Sectors[0].Surfaces[0];
        surface.Corners[0].MonoIntensity = 0.125f;
        surface.Corners[0].Intensity = new Sed.Core.Math.ColorF(0.25f, 0.5f, 0.75f);
        File.WriteAllText(input, JklWriter.Build(doc));
        var vertices = surface.Corners.Select(c => c.Vertex.Position).ToArray();
        var plan = Plan(input, """
            [{"op":"setSurfaceUv","sector":0,"surface":0,"uv":[[-0.0,0],[256,0],[256,32],[0,32]]},
             {"op":"setSurfaceRenderModes","sector":0,"surface":0,"geometryMode":4,"lightMode":3,"textureMode":3}]
            """);
        var output = JklParser.ParseDocument(plan.OutputText).Level.Sectors[0].Surfaces[0];
        Assert.Equal(vertices, output.Corners.Select(c => c.Vertex.Position).ToArray());
        Assert.Equal(0.125f, output.Corners[0].MonoIntensity);
        Assert.Equal(new Sed.Core.Math.ColorF(0.25f, 0.5f, 0.75f), output.Corners[0].Intensity);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(output.Corners[0].Uv.U));
        Assert.Equal(256, output.Corners[2].Uv.U); Assert.Equal(32, output.Corners[2].Uv.V);
    }

    [Theory]
    [InlineData("[{\"op\":\"registerMaterial\",\"name\":\"../wall.mat\"}]")]
    [InlineData("[{\"op\":\"registerMaterial\",\"name\":\"wall.cmp\"}]")]
    [InlineData("[{\"op\":\"registerMaterial\",\"name\":\"wall,extra.mat\"}]")]
    [InlineData("[{\"op\":\"registerColormap\",\"name\":\"wall=extra.cmp\"}]")]
    [InlineData("[{\"op\":\"registerColormap\",\"name\":\"wall.mat\"}]")]
    [InlineData("[{\"op\":\"registerMaterial\",\"name\":\"wall.mat\"},{\"op\":\"registerMaterial\",\"name\":\"WALL.MAT\"}]")]
    [InlineData("[{\"op\":\"setSectorColormap\",\"sector\":0,\"colormapIndex\":0}]")]
    [InlineData("[{\"op\":\"fitSurfaceTexture\",\"sector\":0,\"surface\":0,\"width\":0,\"height\":64}]")]
    [InlineData("[{\"op\":\"setSurfaceUv\",\"sector\":0,\"surface\":0,\"uv\":[[0,0],[1,0],[1,1]]}]")]
    [InlineData("[{\"op\":\"setSurfaceUv\",\"sector\":0,\"surface\":0,\"uv\":[[0,0],[1,0],[1,1],[1e999,1]]}]")]
    [InlineData("[{\"op\":\"setSurfaceRenderModes\",\"sector\":0,\"surface\":0,\"geometryMode\":5,\"lightMode\":3,\"textureMode\":3}]")]
    public void InvalidResourceOrTextureRequestFailsWithoutOutput(string operations)
    {
        string input = Input(); byte[] before = File.ReadAllBytes(input);
        var error = Assert.Throws<AgentRequestException>(() => Plan(input, operations));
        Assert.NotNull(error.OperationIndex); Assert.StartsWith("/operations/", error.JsonPointer);
        Assert.Equal(before, File.ReadAllBytes(input)); Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void UvCommandUndoRestoresAllCornerCoordinates()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        var surface = level.Sectors[0].Surfaces[0]; var original = surface.Corners.Select(c => c.Uv).ToArray();
        var next = new[] { new TexVertex(1, 2), new TexVertex(3, 4), new TexVertex(5, 6), new TexVertex(7, 8) };
        var command = new SetSurfaceUvCommand(surface, next); command.Apply();
        Assert.Equal(next, surface.Corners.Select(c => c.Uv).ToArray());
        command.Revert(); Assert.Equal(original, surface.Corners.Select(c => c.Uv).ToArray());
    }

    [Fact]
    public void SpatialReadbackSuppliesWorldBoundsNormalsAndRegisteredColormaps()
    {
        string input = Input();
        var edit = Plan(input, """[{"op":"registerColormap","name":"dflt.cmp"}]""");
        string output = Path.Combine(_directory, "query.jkl"); AgentWorkspace.Commit(edit, output);
        JsonElement Query(string request) => JsonSerializer.SerializeToElement(AgentWorkspace.Query(output, request), AgentWorkspace.JsonOptions);
        var surface = Query("""{"schemaVersion":1,"collection":"surfaces","sector":0,"limit":1}""").GetProperty("items")[0];
        Assert.Equal(1, surface.GetProperty("normal")[2].GetDouble());
        Assert.Equal(-1, surface.GetProperty("centroid")[2].GetDouble());
        var sector = Query("""{"schemaVersion":1,"collection":"sectors","limit":1}""").GetProperty("items")[0];
        Assert.Equal(-1, sector.GetProperty("geometryBounds").GetProperty("min")[0].GetDouble());
        Assert.Equal(1, sector.GetProperty("geometryBounds").GetProperty("max")[0].GetDouble());
        Assert.Empty(sector.GetProperty("neighbors").EnumerateArray());
        var colormap = Query("""{"schemaVersion":1,"collection":"colormaps","search":"DFLT"}""").GetProperty("items")[0];
        Assert.Equal("dflt.cmp", colormap.GetProperty("name").GetString());
    }
    public void Dispose() => Directory.Delete(_directory, true);
}

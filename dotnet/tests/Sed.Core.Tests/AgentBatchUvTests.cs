using System.Text.Json;
using Sed.Core.Math;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;
using Xunit;

namespace Sed.Core.Tests;

public sealed class AgentBatchUvTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-batch-uv-" + Guid.NewGuid().ToString("N"));
    public AgentBatchUvTests() => Directory.CreateDirectory(_directory);
    private string Input(string game = "mots")
    {
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".jkl");
        AgentWorkspace.Create(path, game);
        return path;
    }
    private static PreparedAgentEdit Plan(string input, string operations) => AgentWorkspace.Plan(input,
        "{\"schemaVersion\":1,\"inputSha256\":\"" + AgentWorkspace.Load(input).Hash + "\",\"operations\":" + operations + "}");
    private static string Targets(string input)
    {
        var surfaces = AgentWorkspace.Load(input).Document.Level.Sectors[0].Surfaces;
        return JsonSerializer.Serialize(surfaces.Select((s, i) => (s, i)).Where(x => System.Math.Abs(x.s.Normal.Z) > .9)
            .Select(x => new { sector = 0, surface = x.i }).ToArray());
    }
    private static string Projection(string targets) => "{\"op\":\"projectSurfaceUv\",\"surfaces\":" + targets
        + ",\"direction\":\"+z\",\"origin\":[0,0,0],\"texelsPerUnit\":32}";

    [Theory]
    [InlineData("jk")]
    [InlineData("mots")]
    public void SharedProjectionAndTransformCommitAndReopenWithActualFinalEffects(string game)
    {
        string input = Input(game), targets = Targets(input);
        var document = AgentWorkspace.Load(input).Document;
        if (game == "mots")
            foreach (var corner in document.Level.Sectors[0].Surfaces.SelectMany(s => s.Corners))
            { corner.MonoIntensity = .125f; corner.Intensity = new ColorF(.25f, .5f, .75f); }
        File.WriteAllText(input, JklWriter.Build(document));
        byte[] original = File.ReadAllBytes(input);
        string transform = "{\"op\":\"transformSurfaceUv\",\"surfaces\":" + targets
            + ",\"pivot\":[0,0],\"scale\":[2,3],\"rotationDegrees\":0,\"offset\":[5,-7]}";
        var edit = Plan(input, "[" + Projection(targets) + "," + transform + "]");
        string output = Path.Combine(_directory, "result.jkl");
        AgentWorkspace.Commit(edit, output);
        var result = AgentWorkspace.Load(output).Document;
        foreach (var s in result.Level.Sectors[0].Surfaces.Where(s => System.Math.Abs(s.Normal.Z) > .9))
            foreach (var c in s.Corners)
            {
                Assert.Equal(c.Vertex.Position.X * 64 + 5, c.Uv.U, 10);
                Assert.Equal(c.Vertex.Position.Y * 96 - 7, c.Uv.V, 10);
                if (game == "mots")
                {
                    Assert.Equal(.125f, c.MonoIntensity);
                    Assert.Equal(new ColorF(.25f, .5f, .75f), c.Intensity);
                }
            }
        var report = JsonSerializer.SerializeToElement(edit.Report, AgentWorkspace.JsonOptions);
        var changes = report.GetProperty("changes");
        Assert.Equal(changes[0].GetProperty("serializedFinal").GetRawText(), changes[1].GetProperty("serializedFinal").GetRawText());
        Assert.NotEqual(changes[0].GetProperty("afterOperation").GetRawText(), changes[0].GetProperty("serializedFinal").GetRawText());
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("[{\"sector\":0,\"surface\":0},{\"sector\":0,\"surface\":0}]")]
    [InlineData("[{\"sector\":0,\"surface\":999}]")]
    [InlineData("[{\"sector\":0,\"surface\":0,\"guess\":true}]")]
    [InlineData("[]")]
    public void InvalidTargetsCannotProduceOutput(string targets)
    {
        string input = Input(); byte[] original = File.ReadAllBytes(input);
        Assert.Throws<AgentRequestException>(() => Plan(input, "[" + Projection(targets) + "]"));
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void BadSecondAddressPointsToActualNestedField()
    {
        string input = Input();
        var error = Assert.Throws<AgentRequestException>(() => Plan(input,
            "[" + Projection("[{\"sector\":0,\"surface\":0},{\"sector\":-1,\"surface\":0}]") + "]"));
        Assert.Equal("/operations/0/surfaces/1/sector", error.JsonPointer);
    }

    [Fact]
    public void RepeatedEditsCountTowardWholePatchCornerBudget()
    {
        string input = Input(), targets = Targets(input);
        Assert.Equal(2, JsonDocument.Parse(targets).RootElement.GetArrayLength());
        string operations = "[" + string.Join(",", Enumerable.Repeat(Projection(targets), 513)) + "]";
        var error = Assert.Throws<AgentRequestException>(() => Plan(input, operations));
        Assert.Equal(512, error.OperationIndex);
        Assert.Contains("4096", error.Message);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void OverflowZeroScaleAndUnknownFieldsRefuseWithNoWrites()
    {
        string input = Input(), targets = Targets(input);
        string common = "{\"op\":\"transformSurfaceUv\",\"surfaces\":" + targets
            + ",\"pivot\":[0,0],\"rotationDegrees\":0,\"offset\":[0,0],\"scale\":";
        Assert.Throws<AgentRequestException>(() => Plan(input, "[" + common + "[0,1]}]"));
        string overflow = Projection(targets).Replace("\"origin\":[0,0,0]", "\"origin\":[-1000000,0,0]");
        var error = Assert.Throws<AgentRequestException>(() => Plan(input, "[" + overflow + "]"));
        Assert.Contains("UV", error.Message);
        string projection = Projection(targets).Replace("\"texelsPerUnit\":32", "\"texelsPerUnit\":32,\"duplicate\":1");
        Assert.Throws<AgentRequestException>(() => Plan(input, "[" + projection + "]"));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void BatchSchemasAdvertiseExactTargetAndCoordinateContracts()
    {
        var schema = JsonSerializer.SerializeToElement(AgentSchema.Patch("projectSurfaceUv"));
        var item = schema.GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("oneOf")[0];
        Assert.Equal(256, item.GetProperty("properties").GetProperty("surfaces").GetProperty("maxItems").GetInt32());
        Assert.Equal(0, item.GetProperty("properties").GetProperty("texelsPerUnit").GetProperty("exclusiveMinimum").GetInt32());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

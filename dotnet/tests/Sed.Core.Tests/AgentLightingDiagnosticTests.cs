using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class AgentLightingDiagnosticTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-light-query-" + Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(_directory, "lights.jkl");
    public AgentLightingDiagnosticTests()
    {
        Directory.CreateDirectory(_directory);
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.Lights.Add(new Light { Position = Vec3.Zero, Range = 8, Intensity = 1, Color = ColorF.White });
        level.Lights.Add(new Light { Position = Vec3.Zero, Range = 0, Intensity = 1, Color = ColorF.White });
        File.WriteAllText(Input, JklWriter.Build(new JklDocument(level)));
    }

    [Fact]
    public void DiagnosticPageBindsSourceAndExplainsTheActualLightIndexWithoutBaking()
    {
        byte[] before = File.ReadAllBytes(Input);
        var page = JsonSerializer.SerializeToElement(AgentWorkspace.DiagnoseLighting(Input,
            """{"schemaVersion":1,"sector":0,"offset":1,"limit":1,"castShadows":false,"updateSectorAmbients":false}"""), AgentWorkspace.JsonOptions);
        Assert.Equal(2, page.GetProperty("total").GetInt32());
        Assert.False(page.GetProperty("castShadows").GetBoolean());
        Assert.False(page.GetProperty("updateSectorAmbients").GetBoolean());
        Assert.Equal(AgentWorkspace.Hash(before), page.GetProperty("sourceRevision").GetString());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(1, item.GetProperty("lightIndex").GetInt32());
        Assert.Equal("DisabledRange", item.GetProperty("status").GetString());
        Assert.Equal(before, File.ReadAllBytes(Input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"castShadows":0}""")]
    [InlineData("""{"schemaVersion":1,"limit":201}""")]
    [InlineData("""{"schemaVersion":1,"sector":-1}""")]
    [InlineData("""{"schemaVersion":1,"offset":-1}""")]
    [InlineData("""{"schemaVersion":1,"profile":"retail"}""")]
    public void UnsupportedRequestsRefuse(string request) =>
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.DiagnoseLighting(Input, request));

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class AgentPlacementArrayTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-array-tests-" + Guid.NewGuid().ToString("N"));
    public AgentPlacementArrayTests() => Directory.CreateDirectory(_directory);
    private string PathFor(string name) => Path.Combine(_directory, name);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private static string Patch(string path, string operations)
    {
        var source = AgentWorkspace.LoadWithProvenance(path);
        return $$"""{"schemaVersion":1,"inputSha256":"{{source.InputSha256}}","sourceRevision":"{{source.SourceRevision}}","operations":{{operations}}}""";
    }
    private string Room()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.Layers.AddRange(["Main", "Array"]);
        level.Templates.Add("crate", new Template { Name = "crate", Parent = "none" });
        string path = PathFor("room.jkl"); File.WriteAllText(path, JklWriter.Build(new JklDocument(level))); return path;
    }

    [Fact]
    public void PlanApplyAndReopenReturnStableNamedGridAndAllFinalCreatedIndices()
    {
        string input = Room(); byte[] before = File.ReadAllBytes(input);
        string operations = """
            [{"op":"addThingArray","template":"crate","namePrefix":"probe","layer":1,"orientation":[10,20,30],
              "layout":{"kind":"grid","origin":[-0.5,-0.5,0],"stepU":[0.5,0,0],"stepV":[0,0.5,0],"countU":3,"countV":2}},
             {"op":"setThingName","thing":2,"name":"chosen"}]
            """;
        var plan = AgentWorkspace.Plan(input, Patch(input, operations));
        var repeat = AgentWorkspace.Plan(input, Patch(input, operations)); Assert.Equal(plan.OutputText, repeat.OutputText);
        Assert.Equal(before, File.ReadAllBytes(input)); Assert.Single(Directory.GetFiles(_directory));
        var change = Json(plan.Report).GetProperty("changes")[0];
        Assert.Equal(JsonValueKind.Null, change.GetProperty("before").ValueKind);
        var final = change.GetProperty("serializedFinal").GetProperty("items");
        Assert.Equal(6, final.GetArrayLength());
        Assert.Equal(Enumerable.Range(0, 6), final.EnumerateArray().Select(item => item.GetProperty("target").GetProperty("index").GetInt32()));
        Assert.Equal("probe_002", change.GetProperty("afterOperation").GetProperty("items")[2].GetProperty("state").GetProperty("name").GetString());
        Assert.Equal("chosen", final[2].GetProperty("state").GetProperty("name").GetString());
        string output = PathFor("output.jkl"); AgentWorkspace.Commit(plan, output);
        var reopened = AgentWorkspace.Load(output).Document.Level;
        Assert.Equal(6, reopened.Things.Count); Assert.Equal(256, reopened.ThingCapacity);
        Assert.Equal(new Vec3(.5, 0, 0), reopened.Things[5].Position);
        Assert.All(reopened.Things, thing => { Assert.Equal(1, thing.Layer); Assert.Equal(-1, thing.ArchLightingIndex); Assert.Empty(thing.Values); });
        Assert.Equal(before, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("null", null, 3d)] [InlineData("0", 0d, 0d)] [InlineData("1", 1d, 1d)]
    public void RadialLightsRoundtripIndependentChannelsAndLaterWhiteEdit(string channel, double? stored, double effective)
    {
        string input = Room();
        string operations = $$$"""
            [{"op":"addLightArray","rgb":[0.25,0.5,0.75],"intensity":1,"range":8,"rgbIntensity":{{{channel}}},"legacyRgbRange":99,"flags":2147483649,"layer":1,
              "layout":{"kind":"radial","origin":[0,0,0],"axis":[0,0,1],"radius":0.5,"count":4,"startDegrees":0,"sweepDegrees":360}},
             {"op":"setLight","light":2,"intensity":3}]
            """;
        var plan = AgentWorkspace.Plan(input, Patch(input, operations));
        string output = PathFor("lights.jkl"); AgentWorkspace.Commit(plan, output);
        var lights = AgentWorkspace.Load(output).Document.Level.Lights;
        Assert.Equal(4, lights.Count);
        Assert.All(lights, light => { Assert.Equal(stored, light.RgbIntensity); Assert.Equal(99, light.LegacyRgbRange); Assert.Equal(0x80000001L, light.Flags); });
        Assert.Equal(effective, lights[2].EffectiveRgbIntensity);
        var receipt = Json(plan.Report).GetProperty("changes")[0].GetProperty("serializedFinal");
        Assert.Equal(4, receipt.GetProperty("count").GetInt32());
        Assert.Equal(effective, receipt.GetProperty("items")[2].GetProperty("state").GetProperty("effectiveRgbIntensity").GetDouble());
    }

    [Theory]
    [InlineData("{\"kind\":\"linear\",\"origin\":[0,0,0],\"step\":[1,0,0],\"count\":2}")]
    [InlineData("{\"kind\":\"linear\",\"origin\":[0,0,0],\"step\":[0,0,0],\"count\":2}")]
    [InlineData("{\"kind\":\"grid\",\"origin\":[0,0,0],\"stepU\":[0.01,0,0],\"stepV\":[0,0.01,0],\"countU\":17,\"countV\":16}")]
    [InlineData("{\"kind\":\"linear\",\"origin\":[0,0,0],\"step\":[0.1,0,0],\"count\":2,\"unexpected\":1}")]
    public void InvalidArrayRejectsWholePlanAfterEarlierValidOperationWithoutWriting(string layout)
    {
        string input = Room(); byte[] original = File.ReadAllBytes(input);
        string operations = $$$"""[{"op":"setGravity","value":7},{"op":"addThingArray","template":"crate","namePrefix":"bad","layout":{{{layout}}}}]""";
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(input, Patch(input, operations)));
        Assert.Equal(original, File.ReadAllBytes(input)); Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void GeneratedNameCollisionAndCapacityAreRefusedInComposedPatch()
    {
        string input = Room(); byte[] original = File.ReadAllBytes(input);
        string operation = """{"op":"addThingArray","template":"crate","namePrefix":"same","layout":{"kind":"linear","origin":[0,0,0],"step":[0.1,0,0],"count":2}}""";
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(input, Patch(input, "[" + operation + "," + operation + "]")));
        var limited = AgentWorkspace.Load(input).Document; limited.Level.ThingCapacity = 1;
        string constrained = PathFor("capacity.jkl"); File.WriteAllText(constrained, JklWriter.Build(limited));
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(constrained, Patch(constrained, "[" + operation + "]")));
        Assert.Equal(original, File.ReadAllBytes(input)); Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void SchemaExposesThreeBoundedLayoutsAndFinitePrefixSpace()
    {
        var schema = Json(AgentSchema.Patch("addThingArray"));
        var operation = schema.GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("oneOf")[0];
        var properties = operation.GetProperty("properties");
        Assert.Equal(123, properties.GetProperty("namePrefix").GetProperty("maxLength").GetInt32());
        var layouts = properties.GetProperty("layout").GetProperty("oneOf");
        Assert.Equal(new[] { "linear", "grid", "radial" }, layouts.EnumerateArray()
            .Select(layout => layout.GetProperty("properties").GetProperty("kind").GetProperty("const").GetString()));
        Assert.Equal(256, layouts[0].GetProperty("properties").GetProperty("count").GetProperty("maximum").GetInt32());
        Assert.Equal(256, layouts[1].GetProperty("properties").GetProperty("countU").GetProperty("maximum").GetInt32());
        Assert.Equal(256, layouts[1].GetProperty("properties").GetProperty("countV").GetProperty("maximum").GetInt32());
        Assert.Equal(256, layouts[2].GetProperty("properties").GetProperty("count").GetProperty("maximum").GetInt32());
    }

    public void Dispose() => Directory.Delete(_directory, true);
}

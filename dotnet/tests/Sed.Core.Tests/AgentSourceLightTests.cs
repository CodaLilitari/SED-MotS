using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

/// <summary>Original synthetic projects exercise source lights across the agent transaction boundary.</summary>
public sealed class AgentSourceLightTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-source-light-agent-" + Guid.NewGuid().ToString("N"));
    public AgentSourceLightTests() => Directory.CreateDirectory(_directory);
    private string PathFor(string name) => Path.Combine(_directory, name);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private static string Patch(string path, string operations)
    {
        var source = AgentWorkspace.LoadWithProvenance(path);
        return $$"""{"schemaVersion":1,"inputSha256":"{{source.InputSha256}}","sourceRevision":"{{source.SourceRevision}}","operations":{{operations}}}""";
    }
    private static JsonElement Lights(string path) => Json(AgentWorkspace.Query(path, """{"schemaVersion":1,"collection":"lights"}""")).GetProperty("items");
    private string Room()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.Layers.AddRange(["Main", "Accent"]);
        string path = PathFor("room.jkl");
        File.WriteAllText(path, JklWriter.Build(new JklDocument(level)));
        return path;
    }
    private string Native(string signature, bool mots)
    {
        string path = PathFor("source.jed");
        File.WriteAllText(path, signature + "\n" + (mots ? "SEC: MOTS\nEND\n" : "") + """
            SEC: HEADER
            HDR 1 4 15 2 3 - 320
            HOR 100 768 0 0
            CEIL 0 0
            MIP 1 2 3 4
            LOD 0.1 0.2 0.3 0.4
            END
            SEC: LIGHTS
            LTS 1
            1 2 3 1 0.25 8 80000001 99 1.5 0.25 0.5 0.75
            END
            SEC: LAYERS
            LAYERS 2
            Main
            Accent
            END
            """);
        return path;
    }

    [Theory]
    [InlineData("JED 0.95", true)]
    [InlineData("NED 0.95", true)]
    [InlineData("SED 0.3.0", true)]
    [InlineData("JED 0.95", false)]
    public void NativeConversionAndUnrelatedEditsPreserveEverySourceChannel(string signature, bool mots)
    {
        string input = Native(signature, mots);
        byte[] original = File.ReadAllBytes(input);
        var conversion = AgentWorkspace.ConvertDocument(input);
        string converted = PathFor("converted.jkl");
        AgentWorkspace.Commit(conversion, converted);
        string edited = PathFor("edited.jkl");
        var move = AgentWorkspace.Plan(converted, Patch(converted, """[{"op":"setLight","light":0,"position":[4,5,6]}]"""));
        var report = Json(move.Report).GetProperty("changes")[0];
        Assert.Equal(1, report.GetProperty("before").GetProperty("state").GetProperty("position")[0].GetDouble());
        Assert.Equal(4, report.GetProperty("serializedFinal").GetProperty("state").GetProperty("position")[0].GetDouble());
        AgentWorkspace.Commit(move, edited);
        string final = PathFor("gravity.jkl");
        AgentWorkspace.Commit(AgentWorkspace.Plan(edited, Patch(edited, """[{"op":"setGravity","value":6}]""")), final);
        var light = Assert.Single(AgentWorkspace.Load(final).Document.Level.Lights);
        Assert.Equal(.25, light.Intensity);
        Assert.Equal(1.5, light.RgbIntensity);
        Assert.Equal(99, light.LegacyRgbRange);
        Assert.Equal(8, light.Range);
        Assert.Equal(new ColorF(.25f, .5f, .75f), light.Color);
        Assert.Equal(new Vec3(4, 5, 6), light.Position);
        Assert.Equal(0x80000001L, light.Flags);
        Assert.Equal(1, light.Layer);
        Assert.Equal("Accent", AgentWorkspace.Load(final).Document.Level.Layers[light.Layer]);
        var readback = Assert.Single(Lights(final).EnumerateArray());
        Assert.Equal(.25, readback.GetProperty("intensity").GetDouble());
        Assert.Equal(1.5, readback.GetProperty("rgbIntensity").GetDouble());
        Assert.Equal(1.5, readback.GetProperty("effectiveRgbIntensity").GetDouble());
        Assert.False(readback.GetProperty("rgbIntensityInherited").GetBoolean());
        Assert.Equal(99, readback.GetProperty("legacyRgbRange").GetDouble());
        Assert.Equal(0x80000001L, readback.GetProperty("flags").GetInt64());
        Assert.Equal(1, readback.GetProperty("layer").GetInt32());
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Theory]
    [InlineData("null", null, 3)]
    [InlineData("0", 0.0, 0)]
    [InlineData("1", 1.0, 1)]
    public void AddThenEditDistinguishesInheritedZeroAndExplicitEqualIntensity(string rgbJson, double? expectedRgb, double effective)
    {
        string input = Room();
        byte[] original = File.ReadAllBytes(input);
        string operations = $$"""[{"op":"addLight","position":[0,0,0],"rgb":[0.25,0.5,0.75],"intensity":1,"range":8,"rgbIntensity":{{rgbJson}},"legacyRgbRange":99,"flags":2147483649,"layer":1},{"op":"setLight","light":0,"intensity":3}]""";
        var plan = AgentWorkspace.Plan(input, Patch(input, operations));
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Single(Directory.GetFiles(_directory));
        var light = Assert.Single(JklParser.ParseDocument(plan.OutputText).Level.Lights);
        Assert.Equal(expectedRgb, light.RgbIntensity);
        Assert.Equal(effective, light.EffectiveRgbIntensity);
        Assert.Equal(99, light.LegacyRgbRange);
        var changes = Json(plan.Report).GetProperty("changes");
        Assert.Equal(JsonValueKind.Null, changes[0].GetProperty("before").ValueKind);
        Assert.Equal(0, changes[0].GetProperty("serializedFinal").GetProperty("target").GetProperty("index").GetInt32());
        Assert.Equal(changes[0].GetProperty("serializedFinal").GetRawText(), changes[1].GetProperty("serializedFinal").GetRawText());
        Assert.Equal(1, changes[0].GetProperty("afterOperation").GetProperty("state").GetProperty("intensity").GetDouble());
        Assert.Equal(3, changes[1].GetProperty("serializedFinal").GetProperty("state").GetProperty("intensity").GetDouble());
        Assert.Equal(effective, changes[1].GetProperty("serializedFinal").GetProperty("state").GetProperty("effectiveRgbIntensity").GetDouble());
        string output = PathFor("created.jkl");
        AgentWorkspace.Commit(plan, output);
        var query = Assert.Single(Lights(output).EnumerateArray());
        if (expectedRgb is null) Assert.Equal(JsonValueKind.Null, query.GetProperty("rgbIntensity").ValueKind);
        else Assert.Equal(expectedRgb.Value, query.GetProperty("rgbIntensity").GetDouble());
        Assert.Equal(effective, query.GetProperty("effectiveRgbIntensity").GetDouble());
        Assert.Equal(expectedRgb is null, query.GetProperty("rgbIntensityInherited").GetBoolean());
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, output));
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public void JkRetainsInactiveImportedRgbButOnlyAllowsClearingItsOverride()
    {
        string input = Native("JED 0.95", false);
        byte[] original = File.ReadAllBytes(input);
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(input,
            Patch(input, """[{"op":"setLight","light":0,"rgbIntensity":0}]""")));
        var plan = AgentWorkspace.Plan(input, Patch(input,
            """[{"op":"setLight","light":0,"rgbIntensity":null,"legacyRgbRange":-24}]"""));
        var light = Assert.Single(JklParser.Parse(plan.OutputText).Lights);
        Assert.Null(light.RgbIntensity);
        Assert.Equal(.25, light.EffectiveRgbIntensity);
        Assert.Equal(-24, light.LegacyRgbRange);
        Assert.Equal(new ColorF(.25f, .5f, .75f), light.Color);
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public void VersionOneAddDefaultsRemainCompatibleAndNullExplicitlyRestoresInheritance()
    {
        string input = Room();
        var plan = AgentWorkspace.Plan(input, Patch(input, """
            [{"op":"addLight","position":[0,0,0],"rgb":[1,1,1],"intensity":2,"range":4},
             {"op":"setLight","light":0,"rgbIntensity":0,"legacyRgbRange":0},
             {"op":"setLight","light":0,"rgbIntensity":null,"legacyRgbRange":null,"intensity":5,"rgb":[0.5,0.25,0.75],"range":6,"flags":1,"layer":1}]
            """));
        var states = Json(plan.Report).GetProperty("changes");
        Assert.Equal(JsonValueKind.Null, states[0].GetProperty("afterOperation").GetProperty("state").GetProperty("rgbIntensity").ValueKind);
        Assert.Equal(0, states[1].GetProperty("afterOperation").GetProperty("state").GetProperty("legacyRgbRange").GetDouble());
        var light = Assert.Single(JklParser.Parse(plan.OutputText).Lights);
        Assert.Null(light.RgbIntensity);
        Assert.Null(light.LegacyRgbRange);
        Assert.Equal(5, light.EffectiveRgbIntensity);
        Assert.Equal(6, light.Range);
        Assert.Equal(new ColorF(.5f, .25f, .75f), light.Color);
        Assert.Equal(1, light.Flags);
        Assert.Equal(1, light.Layer);
    }

    [Theory]
    [InlineData("{\"op\":\"setLight\",\"light\":0}")]
    [InlineData("{\"op\":\"setLight\",\"light\":-1,\"intensity\":1}")]
    [InlineData("{\"op\":\"setLight\",\"light\":99,\"intensity\":1}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"rgbIntensity\":\"1\"}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"rgbIntensity\":-1}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"rgbIntensity\":1001}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"rgbIntensity\":1e999}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"rgbIntensity\":0,\"rgbIntensity\":null}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"legacyRgbRange\":true}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"legacyRgbRange\":1e999}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"range\":0}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"intensity\":null}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"flags\":4294967296}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"layer\":-1}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"layer\":99}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"layer\":0.5}")]
    [InlineData("{\"op\":\"setLight\",\"light\":0,\"rgbIntensity\":0,\"shell\":\"ignored\"}")]
    public void InvalidLaterLightPatchDoesNotPersistEarlierCreation(string invalid)
    {
        string input = Room();
        byte[] original = File.ReadAllBytes(input);
        const string add = """{"op":"addLight","position":[0,0,0],"rgb":[1,1,1],"intensity":1,"range":4}""";
        var error = Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(input, Patch(input, "[" + add + "," + invalid + "]")));
        Assert.Equal(1, error.OperationIndex);
        Assert.Equal("setLight", error.Operation);
        Assert.StartsWith("/operations/1", error.JsonPointer);
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Empty(AgentWorkspace.Load(input).Document.Level.Lights);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void NativeSourceAndTemplateStalenessPreventLightEditAndCommit()
    {
        string input = Native("JED 0.95", true);
        string request = Patch(input, """[{"op":"setLight","light":0,"rgbIntensity":0}]""");
        var plan = AgentWorkspace.Plan(input, request);
        File.WriteAllText(PathFor("mots.tpl"), "synthetic none type=ghost\n");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(input, request));
        string output = PathFor("stale.jkl");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, output));
        Assert.False(File.Exists(output));
        string refreshed = Patch(input, """[{"op":"setLight","light":0,"rgbIntensity":0}]""");
        var refreshedPlan = AgentWorkspace.Plan(input, refreshed);
        File.AppendAllText(input, "\n# external source change\n");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(input, refreshed));
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(refreshedPlan, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void SchemaAdvertisesEditableLightFieldsAndKeepsOldAddRequirements()
    {
        JsonElement Variant(string op) => Json(AgentSchema.Patch(op)).GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("oneOf")[0];
        var add = Variant("addLight");
        Assert.Equal(new[] { "intensity", "op", "position", "range", "rgb" }, add.GetProperty("required").EnumerateArray().Select(v => v.GetString()!).Order().ToArray());
        var edit = Variant("setLight");
        Assert.Equal(new[] { "light", "op" }, edit.GetProperty("required").EnumerateArray().Select(v => v.GetString()!).Order().ToArray());
        Assert.Equal(3, edit.GetProperty("minProperties").GetInt32());
        foreach (var variant in new[] { add, edit })
        {
            Assert.False(variant.GetProperty("additionalProperties").GetBoolean());
            var properties = variant.GetProperty("properties");
            foreach (string field in new[] { "rgbIntensity", "legacyRgbRange", "flags", "layer", "position", "intensity", "range", "rgb" })
                Assert.True(properties.TryGetProperty(field, out _), $"Missing discoverable source-light field {field}.");
            Assert.Contains("null", properties.GetProperty("rgbIntensity").GetRawText());
            Assert.Contains("null", properties.GetProperty("legacyRgbRange").GetRawText());
        }
    }

    [Theory]
    [InlineData("", 1.5, 99.0)]
    [InlineData(",\"rgbIntensity\":null,\"legacyRgbRange\":null", null, null)]
    [InlineData(",\"rgbIntensity\":0,\"legacyRgbRange\":0", 0.0, 0.0)]
    [InlineData(",\"rgbIntensity\":1.5,\"legacyRgbRange\":99", 1.5, 99.0)]
    public void RequestPresenceSurvivesPlanCommitAndReadback(string optionalFields, double? rgb, double? legacyRange)
    {
        string input = Native("JED 0.95", true);
        byte[] original = File.ReadAllBytes(input);
        string request = Patch(input, "[{\"op\":\"setLight\",\"light\":0,\"intensity\":3" + optionalFields + "}]");
        var plan = AgentWorkspace.Plan(input, request);
        var report = Json(plan.Report);
        AssertMetadata(report.GetProperty("sourceLightMetadata"), "absent");
        AssertMetadata(report.GetProperty("outputSourceLightMetadata"), rgb.HasValue || legacyRange.HasValue ? "validated" : "absent");
        var change = report.GetProperty("changes")[0];
        Assert.Equal(1.5, change.GetProperty("before").GetProperty("state").GetProperty("rgbIntensity").GetDouble());
        Assert.Equal(99, change.GetProperty("before").GetProperty("state").GetProperty("legacyRgbRange").GetDouble());
        string output = PathFor("presence.jkl");
        // Replan like the real apply command, binding its write to the reviewed bytes.
        AgentWorkspace.Commit(AgentWorkspace.Plan(input, request), output, report.GetProperty("outputSha256").GetString());
        var light = Assert.Single(Lights(output).EnumerateArray());
        foreach (var state in new[] { change.GetProperty("afterOperation").GetProperty("state"), change.GetProperty("serializedFinal").GetProperty("state"), light })
        {
            AssertOptional(state.GetProperty("rgbIntensity"), rgb);
            AssertOptional(state.GetProperty("legacyRgbRange"), legacyRange);
            Assert.Equal(rgb ?? 3, state.GetProperty("effectiveRgbIntensity").GetDouble());
            Assert.Equal(!rgb.HasValue, state.GetProperty("rgbIntensityInherited").GetBoolean());
        }
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public void JsonPropertyOrderDoesNotChangeReviewedLightOutput()
    {
        string input = Native("JED 0.95", true);
        var source = AgentWorkspace.LoadWithProvenance(input);
        const string firstOperations = """
            [{"op":"addLight","position":[0,0,0],"rgb":[1,1,1],"intensity":2,"range":4,"rgbIntensity":2,"legacyRgbRange":null,"flags":1,"layer":1},
             {"op":"setLight","light":1,"intensity":3,"rgbIntensity":0,"legacyRgbRange":null}]
            """;
        const string reorderedOperations = """
            [{"layer":1,"flags":1,"legacyRgbRange":null,"rgbIntensity":2,"range":4,"intensity":2,"rgb":[1,1,1],"position":[0,0,0],"op":"addLight"},
             {"legacyRgbRange":null,"rgbIntensity":0,"intensity":3,"light":1,"op":"setLight"}]
            """;
        string first = Patch(input, firstOperations);
        string reordered = $$"""{"operations":{{reorderedOperations}},"sourceRevision":"{{source.SourceRevision}}","inputSha256":"{{source.InputSha256}}","schemaVersion":1}""";
        var plan = AgentWorkspace.Plan(input, first);
        var reorderedPlan = AgentWorkspace.Plan(input, reordered);
        Assert.Equal(plan.OutputText, reorderedPlan.OutputText);
        string expectedHash = Json(plan.Report).GetProperty("outputSha256").GetString()!;
        string output = PathFor("reordered.jkl");
        AgentWorkspace.Commit(reorderedPlan, output, expectedHash);
        Assert.Equal(expectedHash, AgentWorkspace.Hash(File.ReadAllBytes(output)));
        var light = Lights(output)[1];
        Assert.Equal(3, light.GetProperty("intensity").GetDouble());
        Assert.Equal(0, light.GetProperty("rgbIntensity").GetDouble());
        Assert.Equal(JsonValueKind.Null, light.GetProperty("legacyRgbRange").ValueKind);
    }

    [Fact]
    public void MetadataReadbackDistinguishesInputAbsenceFromValidatedConversionOutput()
    {
        string native = Native("JED 0.95", true);
        var conversion = AgentWorkspace.ConvertDocument(native);
        AssertMetadata(Json(conversion.Report).GetProperty("sourceLightMetadata"), "absent");
        AssertMetadata(Json(conversion.Report).GetProperty("outputSourceLightMetadata"), "validated");
        string converted = PathFor("metadata.jkl");
        AgentWorkspace.Commit(conversion, converted);
        string room = Room();
        foreach (var (path, expected) in new[] { (native, "absent"), (converted, "validated"), (room, "absent") })
        {
            byte[] original = File.ReadAllBytes(path);
            AssertMetadata(Json(AgentWorkspace.Inspect(path)).GetProperty("sourceLightMetadata"), expected);
            AssertMetadata(Json(AgentWorkspace.Validate(path)).GetProperty("sourceLightMetadata"), expected);
            AssertMetadata(Json(AgentWorkspace.Query(path, """{"schemaVersion":1,"collection":"lights"}""")).GetProperty("sourceLightMetadata"), expected);
            AssertMetadata(Json(AgentWorkspace.Query(path, """{"schemaVersion":1,"collection":"summary"}""")).GetProperty("sourceLightMetadata"), expected);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("count")]
    public void InvalidMetadataNeverReturnsAnAbsentOrValidatedReadback(string mutation)
    {
        string input = Native("JED 0.95", true);
        string output = PathFor("malformed.jkl");
        string text = AgentWorkspace.ConvertDocument(input).OutputText;
        int offset = text.IndexOf("SECTION: SED_SOURCE_LIGHTS", StringComparison.Ordinal);
        Assert.True(offset >= 0);
        string metadata = text[offset..];
        metadata = mutation switch
        {
            "version" => metadata.Replace("VERSION 1", "VERSION 999", StringComparison.Ordinal),
            "digest" => System.Text.RegularExpressions.Regex.Replace(metadata, "LIGHTS_SHA256 [0-9a-fA-F]{64}", "LIGHTS_SHA256 " + new string('0', 64)),
            "count" => metadata.Replace("SOURCE_LIGHTS 1", "SOURCE_LIGHTS 2", StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };
        File.WriteAllText(output, text[..offset] + metadata);
        byte[] original = File.ReadAllBytes(output);
        foreach (Action read in new Action[]
        {
            () => AgentWorkspace.Inspect(output),
            () => AgentWorkspace.Query(output, """{"schemaVersion":1,"collection":"lights"}"""),
            () => AgentWorkspace.Validate(output),
            () => AgentWorkspace.ConvertDocument(output),
        })
        {
            var error = Record.Exception(read);
            Assert.True(error is FormatException or NotSupportedException, $"Invalid metadata produced {error?.GetType().Name ?? "no error"}.");
        }
        Assert.Equal(original, File.ReadAllBytes(output));
    }

    private static void AssertOptional(JsonElement actual, double? expected)
    {
        if (expected.HasValue) Assert.Equal(expected.Value, actual.GetDouble());
        else Assert.Equal(JsonValueKind.Null, actual.ValueKind);
    }

    private static void AssertMetadata(JsonElement actual, string state)
    {
        Assert.Equal(new[] { "binding", "state" }, actual.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(state, actual.GetProperty("state").GetString());
        if (state == "validated") Assert.Equal("ordinary-lights-by-index", actual.GetProperty("binding").GetString());
        else Assert.Equal(JsonValueKind.Null, actual.GetProperty("binding").ValueKind);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

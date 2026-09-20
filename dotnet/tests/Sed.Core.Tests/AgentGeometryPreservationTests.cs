using System.Text.Json;
using System.Text.RegularExpressions;
using Sed.Core.Editing;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jed;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

/// <summary>Original native light fixtures cross geometry planning and JKL export boundaries.</summary>
public sealed class AgentGeometryPreservationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-agent-geometry-preservation-" + Guid.NewGuid().ToString("N"));
    private string Input => Path.Combine(_directory, "source.jkl");

    public AgentGeometryPreservationTests() => Directory.CreateDirectory(_directory);

    private void WriteFixture(bool adjacentRoom = false)
    {
        // The short native row inherits white intensity; the other two retain
        // independent zero/equal values and distinct legacy-range metadata.
        var doc = JedImporter.ImportText("""
            JED 0.95
            SEC: MOTS
            END
            SEC: HEADER
            HDR 1 4 15 2 3 - 320
            HOR 100 768 0 0
            CEIL 0 0
            MIP 1 2 3 4
            LOD 0.1 0.2 0.3 0.4
            END
            SEC: LIGHTS
            LTS 3
            -0.5 0 0 0 0.125 4 1 0.25 0.5 0.75
            0 0 0 1 2.25 8 80000001 99 0 0.5 0.25 0.75
            0.5 0 0 2 1.5 6 2 0 1.5 0.75 0.5 0.25
            END
            SEC: LAYERS
            LAYERS 3
            Main
            Dark
            Equal
            END
            """).Document;
        new CreateSectorCommand(doc.Level, SectorFactory.CreateBox(doc.Level, new(0, 0, 0), 1, string.Empty, -1)).Apply();
        if (adjacentRoom)
            new CreateSectorCommand(doc.Level, SectorFactory.CreateBox(doc.Level, new(2, 0, 0), 1, string.Empty, -1)).Apply();
        File.WriteAllText(Input, JklWriter.Build(doc));
    }

    private static string Operation(string name) => name switch
    {
        "addBox" => """{"op":"addBox","center":[2,0,0],"halfSize":1}""",
        "extrudeSurface" => """{"op":"extrudeSurface","sector":0,"surface":5,"distance":2}""",
        "adjoinSurfaces" => """{"op":"adjoinSurfaces","sector":0,"surface":5,"otherSector":1,"otherSurface":4}""",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private string Patch(string operations) => $$"""{"schemaVersion":1,"inputSha256":"{{AgentWorkspace.Hash(File.ReadAllBytes(Input))}}","operations":{{operations}}}""";
    private PreparedAgentEdit Plan(string operations) => AgentWorkspace.Plan(Input, Patch(operations));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);

    private static void AssertSourceLights(Level before, Level after)
    {
        Assert.Equal(3, after.Lights.Count);
        Assert.Null(after.Lights[0].RgbIntensity);
        Assert.Null(after.Lights[0].LegacyRgbRange);
        Assert.Equal(0.125, after.Lights[0].EffectiveRgbIntensity);
        Assert.Equal(0.0, after.Lights[1].RgbIntensity);
        Assert.Equal(0.0, after.Lights[1].EffectiveRgbIntensity);
        Assert.Equal(99.0, after.Lights[1].LegacyRgbRange);
        Assert.Equal(1.5, after.Lights[2].RgbIntensity);
        Assert.Equal(after.Lights[2].Intensity, after.Lights[2].RgbIntensity);
        Assert.Equal(0.0, after.Lights[2].LegacyRgbRange);
        Assert.Equal(before.Layers, after.Layers);
        for (int i = 0; i < before.Lights.Count; i++)
        {
            var expected = before.Lights[i];
            var actual = after.Lights[i];
            Assert.Equal(expected.Position, actual.Position);
            Assert.Equal(expected.Color, actual.Color);
            Assert.Equal(expected.Intensity, actual.Intensity);
            Assert.Equal(expected.Range, actual.Range);
            Assert.Equal(expected.Flags, actual.Flags);
            Assert.Equal(expected.Layer, actual.Layer);
            Assert.Equal(expected.RgbIntensity, actual.RgbIntensity);
            Assert.Equal(expected.LegacyRgbRange, actual.LegacyRgbRange);
        }
    }

    [Theory]
    [InlineData("addBox")]
    [InlineData("extrudeSurface")]
    [InlineData("adjoinSurfaces")]
    public void GeometryPlanningAndReviewedCommitPreserveImportedIndependentSourceLights(string operation)
    {
        WriteFixture(adjacentRoom: operation == "adjoinSurfaces");
        byte[] original = File.ReadAllBytes(Input);
        var before = AgentWorkspace.Load(Input).Document.Level;
        string request = Patch("[" + Operation(operation) + "]");
        var plan = AgentWorkspace.Plan(Input, request);
        var report = Json(plan.Report);
        Assert.Equal("validated", report.GetProperty("sourceLightMetadata").GetProperty("state").GetString());
        Assert.Equal("validated", report.GetProperty("outputSourceLightMetadata").GetProperty("state").GetString());
        var parsed = JklParser.ParseDocument(plan.OutputText).Level;
        Assert.Equal(2, parsed.Sectors.Count);
        AssertSourceLights(before, parsed);
        Assert.Equal(original, File.ReadAllBytes(Input));

        string output = Path.Combine(_directory, "edited.jkl");
        string reviewedHash = report.GetProperty("outputSha256").GetString()!;
        AgentWorkspace.Commit(AgentWorkspace.Plan(Input, request), output, reviewedHash);
        Assert.Equal(reviewedHash, AgentWorkspace.Hash(File.ReadAllBytes(output)));
        var reopened = AgentWorkspace.Load(output).Document.Level;
        AssertSourceLights(before, reopened);
        Assert.True(Json(AgentWorkspace.Validate(output)).GetProperty("valid").GetBoolean());
        if (operation != "addBox")
        {
            var sourceFace = reopened.Sectors[0].Surfaces[5];
            Assert.NotNull(sourceFace.Adjoin);
            Assert.Same(reopened.Sectors[1], sourceFace.Adjoin.Sector);
            Assert.Same(sourceFace, sourceFace.Adjoin.Adjoin);
        }
        Assert.Equal(original, File.ReadAllBytes(Input));
    }

    [Theory]
    [InlineData("addBox")]
    [InlineData("extrudeSurface")]
    [InlineData("adjoinSurfaces")]
    public void GeometryAllowlistDoesNotPermitMismatchedSourceLightChecksums(string operation)
    {
        WriteFixture(adjacentRoom: operation == "adjoinSurfaces");
        string original = File.ReadAllText(Input);
        string corrupt = Regex.Replace(original, "LIGHTS_SHA256 [0-9a-fA-F]{64}", "LIGHTS_SHA256 " + new string('0', 64));
        Assert.NotEqual(original, corrupt);
        File.WriteAllText(Input, corrupt);
        byte[] unchanged = File.ReadAllBytes(Input);
        var error = Assert.Throws<FormatException>(() => Plan("[" + Operation(operation) + "]"));
        Assert.Contains("no longer matches", error.Message);
        Assert.Equal(unchanged, File.ReadAllBytes(Input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void OpaqueSectionRefusesBoxAfterAnEarlierEditWithoutPersistingEitherOperation()
    {
        WriteFixture();
        File.AppendAllText(Input, "\nSECTION: UNKNOWN_TOPOLOGY\n7 9 2\nEND\n");
        byte[] original = File.ReadAllBytes(Input);
        var error = Assert.Throws<AgentRequestException>(() => Plan("""[{"op":"setGravity","value":6},""" + Operation("addBox") + "]"));
        Assert.Contains("UNKNOWN_TOPOLOGY", error.Message);
        Assert.Equal(1, error.OperationIndex);
        Assert.Equal("addBox", error.Operation);
        Assert.Equal(original, File.ReadAllBytes(Input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void LaterGeometryFailureDoesNotPersistEarlierLightOrGeometryEdits()
    {
        WriteFixture();
        byte[] original = File.ReadAllBytes(Input);
        var before = AgentWorkspace.Load(Input).Document.Level;
        string validOperations = "[" + Operation("extrudeSurface") + "]";
        var cleanPlan = Plan(validOperations);
        string operations = """[{"op":"setLight","light":1,"rgbIntensity":3},""" + Operation("extrudeSurface") + "," + Operation("extrudeSurface") + "]";
        var error = Assert.Throws<AgentRequestException>(() => Plan(operations));
        Assert.Equal(2, error.OperationIndex);
        Assert.Equal("extrudeSurface", error.Operation);
        Assert.Contains("already adjoined", error.Message);
        Assert.Equal(original, File.ReadAllBytes(Input));
        Assert.Single(Directory.GetFiles(_directory));
        var reopened = AgentWorkspace.Load(Input).Document.Level;
        Assert.Single(reopened.Sectors);
        AssertSourceLights(before, reopened);

        var retry = Plan(validOperations);
        Assert.Equal(cleanPlan.OutputText, retry.OutputText);
        string cleanHash = Json(cleanPlan.Report).GetProperty("outputSha256").GetString()!;
        Assert.Equal(cleanHash, Json(retry.Report).GetProperty("outputSha256").GetString());
        string output = Path.Combine(_directory, "retry.jkl");
        AgentWorkspace.Commit(retry, output, cleanHash);
        Assert.Equal(cleanHash, AgentWorkspace.Hash(File.ReadAllBytes(output)));
        AssertSourceLights(before, AgentWorkspace.Load(output).Document.Level);
        Assert.Equal(original, File.ReadAllBytes(Input));
    }

    [Theory]
    [InlineData("addBox")]
    [InlineData("extrudeSurface")]
    [InlineData("adjoinSurfaces")]
    public void WriterRefusesDirectGeometryEditsWhenAnOpaqueSectionIsPresent(string operation)
    {
        WriteFixture(adjacentRoom: operation == "adjoinSurfaces");
        File.AppendAllText(Input, "\nSECTION: UNKNOWN_TOPOLOGY\n7 9 2\nEND\n");
        string original = File.ReadAllText(Input);
        var document = AgentWorkspace.Load(Input).Document;
        // Opaque data remains recoverable when geometry is unchanged.
        Assert.Equal(original, JklWriter.Build(document));
        IEditCommand edit = operation switch
        {
            "addBox" => new CreateSectorCommand(document.Level, SectorFactory.CreateBox(document.Level, new(2, 0, 0), 1, string.Empty, -1)),
            "extrudeSurface" => new ExtrudeSurfaceCommand(document.Level.Sectors[0].Surfaces[5], 2),
            "adjoinSurfaces" => new MakeAdjoinCommand(document.Level.Sectors[0].Surfaces[5], document.Level.Sectors[1].Surfaces[4]),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        edit.Apply();
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains("UNKNOWN_TOPOLOGY", error.Message);
        string output = Path.Combine(_directory, "refused.jkl");
        Assert.Throws<InvalidOperationException>(() => JklWriter.Save(document, output));
        Assert.False(File.Exists(output));
        Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal(original, File.ReadAllText(Input));
    }

    [Fact]
    public void UnparsedSourceLightSectionCannotGrantGeometryCapabilityByName()
    {
        WriteFixture();
        var unparsed = new JklDocument(File.ReadAllLines(Input));
        Assert.Contains(unparsed.SourceLines, line => line == "SECTION: SED_SOURCE_LIGHTS");
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(unparsed));
        Assert.Contains("validated source-light metadata", error.Message);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("duplicate-section")]
    [InlineData("duplicate-record")]
    public void UnsupportedOrDuplicatedSourceLightMetadataIsRejectedBeforeGeometryPlanning(string mutation)
    {
        WriteFixture();
        string original = File.ReadAllText(Input);
        int start = original.IndexOf("SECTION: SED_SOURCE_LIGHTS", StringComparison.Ordinal);
        int end = original.IndexOf("\nEND", start, StringComparison.Ordinal) + "\nEND".Length;
        Assert.True(start >= 0 && end > start);
        string metadata = original[start..end];
        string corrupt = mutation switch
        {
            "version" => original[..start] + metadata.Replace("VERSION 1", "VERSION 999", StringComparison.Ordinal) + original[end..],
            "duplicate-section" => original + "\n" + metadata + "\n",
            "duplicate-record" => original[..start] + Regex.Replace(metadata, "(?m)^1:", "0:") + original[end..],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.NotEqual(original, corrupt);
        File.WriteAllText(Input, corrupt);
        foreach (Action attempt in new Action[]
        {
            () => JklParser.ParseDocument(corrupt),
            () => Plan("[" + Operation("addBox") + "]"),
        })
        {
            var error = Record.Exception(attempt);
            if (mutation == "version") Assert.IsType<NotSupportedException>(error);
            else Assert.IsType<FormatException>(error);
        }
        Assert.Equal(corrupt, File.ReadAllText(Input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("GEORESOURCE")]
    [InlineData("SECTORS")]
    public void ExtraSectionHeaderTokensRefuseDirectSaveInsteadOfAppendingDuplicateGeometry(string section)
    {
        WriteFixture();
        string original = File.ReadAllText(Input).Replace("SECTION: " + section, "SECTION: " + section + " extra", StringComparison.Ordinal);
        File.WriteAllText(Input, original);
        var document = AgentWorkspace.Load(Input).Document;
        // The historical reader accepts this header; preserve recovery without
        // permitting regenerated output with duplicate active sections.
        Assert.Equal(original, JklWriter.Build(document));
        new CreateSectorCommand(document.Level, SectorFactory.CreateBox(document.Level, new(2, 0, 0), 1, string.Empty, -1)).Apply();
        string output = Path.Combine(_directory, "refused-header.jkl");
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Save(document, output));
        Assert.Contains("malformed section header", error.Message);
        Assert.Contains(section + " extra", error.Message);
        Assert.False(File.Exists(output));
        Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal(original, File.ReadAllText(Input));
    }

    [Fact]
    public void CommentedSectionHeadersStillPermitValidatedGeometryEdits()
    {
        WriteFixture();
        string original = File.ReadAllText(Input).Replace("SECTION: GEORESOURCE", "SECTION: GEORESOURCE # original author note", StringComparison.Ordinal);
        File.WriteAllText(Input, original);
        var plan = Plan("[" + Operation("extrudeSurface") + "]");
        var reopened = JklParser.ParseDocument(plan.OutputText);
        Assert.Equal(2, reopened.Level.Sectors.Count);
        AssertSourceLights(AgentWorkspace.Load(Input).Document.Level, reopened.Level);
        Assert.Equal(original, File.ReadAllText(Input));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

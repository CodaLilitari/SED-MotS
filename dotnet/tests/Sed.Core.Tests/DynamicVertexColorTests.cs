using System.Text;
using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

/// <summary>Original synthetic storage counterexamples; these do not execute an engine or COG.</summary>
public sealed class DynamicVertexColorTests
{
    [Theory]
    [InlineData(1, 1, 1, 1, VertexRgbStorage.Compacted)]
    [InlineData(1, 1, 1, .25f, VertexRgbStorage.Compacted)]
    [InlineData(.5f, 1, 1, 1, VertexRgbStorage.Retained)]
    [InlineData(1, 1, .5f, 1, VertexRgbStorage.Retained)]
    [InlineData(2, 2, 2, 5, VertexRgbStorage.Compacted)]
    [InlineData(2, 1, 1, 1, VertexRgbStorage.Retained)]
    [InlineData(-1, 0, 0, 0, VertexRgbStorage.Retained)]
    [InlineData(0, -0.0f, 0, 1, VertexRgbStorage.Compacted)]
    public void CoreUsesAllThreeInspectedChannelsWithoutClampingOrBlue(float w, float r, float g, float b, VertexRgbStorage retail)
    {
        var result = DynamicVertexColorReadiness.Evaluate(Enumerable.Repeat(new VertexColorSample(w, r, g, b), 4).ToArray(), 4);
        Assert.Equal(retail, result.Retail);
        Assert.Equal(VertexRgbStorage.Retained, result.OpenMots);
    }

    [Fact]
    public void OneDifferentCornerKeepsStorageForTheWholeSurfaceAndScalarRetailStaysUnknown()
    {
        VertexColorSample[] corners = [new(1, 1, 1, 0), new(1, 1, 1, 1), new(1, .5f, 1, 1), new(1, 1, 1, 1)];
        var result = DynamicVertexColorReadiness.Evaluate(corners, 4);
        Assert.Equal(VertexRgbStorage.Retained, result.Retail);
        Assert.Equal(2, result.DistinguishingCorner);
        result = DynamicVertexColorReadiness.Evaluate(corners, 1);
        Assert.Equal(VertexRgbStorage.Unknown, result.Retail);
        Assert.Equal(VertexRgbStorage.Scalar, result.OpenMots);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void EvenIgnoredBlueMustBeFiniteForThisInference(float blue)
    {
        var result = DynamicVertexColorReadiness.Evaluate(Enumerable.Repeat(new VertexColorSample(1, 1, 1, blue), 3).ToArray(), 4);
        Assert.Equal(VertexRgbStorage.Invalid, result.Retail);
        Assert.Equal(VertexRgbStorage.Invalid, result.OpenMots);
    }

    [Theory]
    [InlineData(2, 4)]
    [InlineData(25, 4)]
    [InlineData(4, 3)]
    public void UninspectedShapeOrLayoutIsInvalid(int count, int components)
    {
        var result = DynamicVertexColorReadiness.Evaluate(Enumerable.Repeat(new VertexColorSample(1, 1, 1, 1), count).ToArray(), components);
        Assert.Equal(VertexRgbStorage.Invalid, result.Retail);
    }

    [Fact]
    public void RetainedMixedRowsAndRegeneratedRowsHaveDifferentStorageInferences()
    {
        string source = ReplaceLighting(RoomText(), 0, "1");
        var document = JklParser.ParseDocument(source);
        string originalCandidate = JklWriter.Build(document);
        var retained = JklDynamicVertexColors.Evaluate(document);
        Assert.Equal("assessed", retained.Status);
        Assert.Equal(Hash(originalCandidate), retained.ProposedOutputSha256);
        Assert.Equal("N", retained.Surfaces[0].Layout);
        Assert.Equal("Unknown", retained.Surfaces[0].Retail);
        Assert.Equal("Scalar", retained.Surfaces[0].OpenMots);
        Assert.All(retained.Surfaces.Skip(1), s => Assert.Equal("4N", s.Layout));
        Assert.Equal(originalCandidate, JklWriter.Build(document));

        document.Level.Sectors[0].Surfaces[1].ExtraLightIntensity = .125f; // Forces GEORESOURCE regeneration.
        string regeneratedCandidate = JklWriter.Build(document);
        var regenerated = JklDynamicVertexColors.Evaluate(document);
        Assert.Equal("assessed", regenerated.Status);
        Assert.NotEqual(retained.ProposedOutputSha256, regenerated.ProposedOutputSha256);
        Assert.Equal(Hash(regeneratedCandidate), regenerated.ProposedOutputSha256);
        Assert.All(regenerated.Surfaces, s => {
            Assert.Equal("4N", s.Layout); Assert.Equal("Compacted", s.Retail); Assert.Equal("Retained", s.OpenMots);
        });
    }

    [Theory]
    [InlineData("1 1.00000001 1 1", "Compacted")]
    [InlineData("1 1.0000001192092896 1 1", "Retained")]
    [InlineData("1 1.000000059604644775390625000001 1 1", "Unknown")]
    [InlineData("1 1.000000059604644775390625 1 1", "Compacted")]
    [InlineData("1 1 1 1.000000059604644775390625000001", "Compacted")]
    [InlineData("0 -0 1e-60 1", "Compacted")]
    [InlineData("3e38 3e38 3e38 0", "Compacted")]
    public void RetainedDecimalRowsUseFloat32AndKeepUnqualifiedRoundingEdgesUnknown(string corner, string expected)
    {
        string source = ReplaceLighting(RoomText(), 0, corner);
        var document = JklParser.ParseDocument(source);
        var result = JklDynamicVertexColors.Evaluate(document);
        Assert.Equal("assessed", result.Status);
        Assert.Equal(Hash(JklWriter.Build(document)), result.ProposedOutputSha256);
        Assert.Equal(expected, result.Surfaces[0].Retail);
    }

    [Fact]
    public void GreyCanVaryBetweenCornersAndOnlyTheLastCornerCanKeepTheSurfaceMutable()
    {
        string text = ReplaceLightingCorners(RoomText(), 0, ["0 0 0 1", ".25 .25 .25 0", ".5 .5 .5 1", "1 1 1 0"]);
        var grey = JklDynamicVertexColors.EvaluateSerialized(text);
        Assert.Equal("assessed", grey.Status); Assert.Equal("Compacted", grey.Surfaces[0].Retail);
        text = ReplaceLightingCorners(text, 0, ["0 0 0 1", ".25 .25 .25 0", ".5 .5 .5 1", "1 .75 1 0"]);
        var colored = JklDynamicVertexColors.EvaluateSerialized(text);
        Assert.Equal("assessed", colored.Status); Assert.Equal("Retained", colored.Surfaces[0].Retail);
        Assert.Equal(3, colored.Surfaces[0].DistinguishingCorner);
        Assert.All(colored.Surfaces.Skip(1), s => Assert.Equal("Compacted", s.Retail));
    }

    [Fact]
    public void AllScalarMotsRetainsItsArchMarkerAndAcceptsMixedCaseWhitespaceHeaders()
    {
        string text = RoomText();
        for (int i = 0; i < 6; i++) text = ReplaceLighting(text, i, "1");
        Assert.Contains("SECTION: ARCHLIGHTING", text);
        text = text.Replace("SECTION: GEORESOURCE", "SeCtIoN:\tGeOrEsOuRcE")
            .Replace("World surfaces ", "wOrLd\tSuRfAcEs\t");
        var document = JklParser.ParseDocument(text);
        Assert.Equal(ProjectType.MysteriesOfTheSith, document.Level.Kind);
        var report = JklDynamicVertexColors.Evaluate(document);
        Assert.Equal("assessed", report.Status); Assert.Equal(6, report.Surfaces.Count);
        Assert.All(report.Surfaces, s => { Assert.Equal("N", s.Layout); Assert.Equal("Unknown", s.Retail); Assert.Equal("Scalar", s.OpenMots); });
    }

    [Fact]
    public void CandidateHashMatchesActualSavedBytesFromUtf8BomCrLfSource()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sed-vertex-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "original.jkl"), output = Path.Combine(directory, "saved.jkl");
            string text = "# caf\u00e9\n" + RoomText();
            var encoding = new UTF8Encoding(true);
            byte[] source = [.. encoding.GetPreamble(), .. encoding.GetBytes(text.Replace("\n", "\r\n"))];
            File.WriteAllBytes(input, source);
            var snapshot = AgentWorkspace.LoadWithProvenance(input);
            var report = JklDynamicVertexColors.Evaluate(snapshot.Document);
            Assert.Equal("assessed", report.Status);
            JklWriter.Save(snapshot.Document, output);
            byte[] saved = File.ReadAllBytes(output);
            Assert.Equal(AgentWorkspace.Hash(saved), report.ProposedOutputSha256);
            Assert.Equal(AgentWorkspace.Hash(source), snapshot.InputSha256);
            Assert.NotEqual(snapshot.InputSha256, report.ProposedOutputSha256);
            Assert.False(saved.AsSpan().StartsWith(encoding.GetPreamble()));
            Assert.DoesNotContain("\r\n", Encoding.UTF8.GetString(saved));
            Assert.Contains("caf\u00e9", Encoding.UTF8.GetString(saved));
            Assert.Equal(source, File.ReadAllBytes(input));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SwappedQuadSectorOwnershipUsesGlobalSourceIdentityAndSectorFilter()
    {
        var level = Room();
        level.Sectors.Add(SectorFactory.CreateBox(level, new Vec3(4, 0, 0), 1, string.Empty, -1));
        level.RenumberSectors();
        string text = ReplaceLighting(JklWriter.Build(new JklDocument(level)), 2, "1 .5 1 1");
        string[] lines = text.Split('\n');
        int first = Array.IndexOf(lines, "SECTOR\t0"), second = Array.IndexOf(lines, "SECTOR\t1");
        int end = Array.FindIndex(lines, second, l => l.Equals("end", StringComparison.OrdinalIgnoreCase));
        Assert.True(first >= 0 && second > first && end > second);
        string[] swapped = [.. lines.Take(first), "SECTOR\t0", .. lines.Skip(second + 1).Take(end - second - 1),
            "SECTOR\t1", .. lines.Skip(first + 1).Take(second - first - 1), .. lines.Skip(end)];
        text = string.Join('\n', swapped);
        var document = JklParser.ParseDocument(text);
        var report = JklDynamicVertexColors.Evaluate(document);
        Assert.Equal("assessed", report.Status); Assert.Equal(12, report.Surfaces.Count);
        var colored = Assert.Single(report.Surfaces, s => s.Retail == "Retained");
        Assert.Equal((2, 1, 2), (colored.GlobalSurfaceIndex, colored.SectorIndex, colored.SurfaceIndex));
        Assert.All(report.Surfaces.Take(6), s => Assert.Equal(1, s.SectorIndex));
        Assert.All(report.Surfaces.Skip(6), s => Assert.Equal(0, s.SectorIndex));
        string directory = Path.Combine(Path.GetTempPath(), "sed-vertex-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "swapped.jkl"); File.WriteAllText(path, text);
            var page = Json(AgentWorkspace.DiagnoseVertexColors(path, """{"schemaVersion":1,"sector":0,"offset":4,"limit":1}"""));
            Assert.Equal(6, page.GetProperty("total").GetInt32());
            var item = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal(10, item.GetProperty("globalSurfaceIndex").GetInt32());
            Assert.Equal(4, item.GetProperty("surfaceIndex").GetInt32());
            Assert.Equal(0, item.GetProperty("sectorIndex").GetInt32());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void IndependentMonoInheritanceAndPerSurfaceCornersSurviveWithoutEdits()
    {
        var level = Room();
        var first = level.Sectors[0].Surfaces[0];
        var corner = first.Corners[0];
        corner.Intensity = new(.5f, .5f, 0);
        var doc = new JklDocument(level);
        var inherited = JklDynamicVertexColors.Evaluate(doc);
        Assert.Equal("Compacted", inherited.Surfaces[0].Retail);
        Assert.Null(corner.MonoIntensity);
        corner.MonoIntensity = 0;
        var explicitZero = JklDynamicVertexColors.Evaluate(doc);
        Assert.Equal("Retained", explicitZero.Surfaces[0].Retail);
        Assert.Equal(0, explicitZero.Surfaces[0].DistinguishingCorner);
        Assert.All(explicitZero.Surfaces.Skip(1), s => Assert.Equal("Compacted", s.Retail));
        Assert.Equal(new ColorF(.5f, .5f, 0), corner.Intensity);
        Assert.Equal(0f, corner.MonoIntensity);
    }

    [Fact]
    public void RenderingModesAndAUserFlagDoNotPromoteRgbStorage()
    {
        var level = Room();
        var surface = level.Sectors[0].Surfaces[0];
        surface.Geo = 0; surface.Light = 0; surface.SurfFlags |= 0x01000000;
        var report = JklDynamicVertexColors.Evaluate(new JklDocument(level));
        Assert.Equal("assessed", report.Status);
        Assert.Equal("Compacted", report.Surfaces[0].Retail);
        Assert.Equal("Retained", report.Surfaces[0].OpenMots);
        Assert.Equal(0, report.Surfaces[0].GeometryMode);
        Assert.Equal(0, report.Surfaces[0].LightingMode);
    }

    [Fact]
    public void AdjoinedCornersRemainIndependentAndAreStillAssessed()
    {
        var level = Room();
        var surface = level.Sectors[0].Surfaces[0];
        new ExtrudeSurfaceCommand(surface, 1).Apply();
        surface.Corners[0].MonoIntensity = .5f;
        var report = JklDynamicVertexColors.Evaluate(new JklDocument(level));
        Assert.Equal("assessed", report.Status);
        var portals = report.Surfaces.Where(s => s.Adjoined).ToArray();
        Assert.Equal(2, portals.Length);
        Assert.Contains(portals, s => s.SectorIndex == 0 && s.Retail == "Retained");
        Assert.Contains(portals, s => s.SectorIndex == 1 && s.Retail == "Compacted");
    }

    [Theory]
    [InlineData("NaN 1 1 1")]
    [InlineData("1 1 1 Infinity")]
    [InlineData("1e40 1 1 1")]
    [InlineData("1 1")]
    public void NonfiniteOrMalformedRowsReturnUnavailableWithExactOutputIdentity(string corner)
    {
        string text = ReplaceLighting(RoomText(), 0, corner);
        var result = JklDynamicVertexColors.EvaluateSerialized(text);
        Assert.Equal("unavailable", result.Status);
        Assert.Equal(Hash(text), result.ProposedOutputSha256);
        Assert.Empty(result.Surfaces);
    }

    [Fact]
    public void InvalidGeometryAndOtherGamesAreNeverReportedReady()
    {
        var level = Room();
        level.Sectors[0].Surfaces[0].Corners[0].Vertex = null!;
        var invalid = JklDynamicVertexColors.Evaluate(new JklDocument(level));
        Assert.Equal("unavailable", invalid.Status); Assert.False(invalid.GeometryValid); Assert.Empty(invalid.Surfaces);
        var jk = JklDynamicVertexColors.Evaluate(new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.JediKnight)));
        Assert.Equal("unsupported-game", jk.Status); Assert.Empty(jk.Surfaces);
    }

    [Fact]
    public void AgentPagesBindSourceAndPlanBindsActualOutputWithoutWriting()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sed-vertex-colors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "room.jkl");
            File.WriteAllText(input, ReplaceLighting(RoomText(), 0, "1"));
            byte[] bytes = File.ReadAllBytes(input);
            var response = Json(AgentWorkspace.DiagnoseVertexColors(input, """{"schemaVersion":1,"sector":0,"offset":1,"limit":2}"""));
            Assert.Equal(AgentWorkspace.Hash(bytes), response.GetProperty("inputSha256").GetString());
            Assert.Equal(AgentWorkspace.Hash(bytes), response.GetProperty("sourceRevision").GetString());
            Assert.Equal(6, response.GetProperty("total").GetInt32());
            Assert.Equal(2, response.GetProperty("returnedCount").GetInt32());
            Assert.Equal(3, response.GetProperty("nextOffset").GetInt32());
            Assert.Equal(new[] { 1, 2 }, response.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("globalSurfaceIndex").GetInt32()));
            var empty = Json(AgentWorkspace.DiagnoseVertexColors(input, """{"schemaVersion":1,"offset":100}"""));
            Assert.Equal(0, empty.GetProperty("returnedCount").GetInt32());
            Assert.Equal(JsonValueKind.Null, empty.GetProperty("nextOffset").ValueKind);
            var plan = AgentWorkspace.Plan(input, JsonSerializer.Serialize(new { schemaVersion = 1,
                inputSha256 = AgentWorkspace.Hash(bytes), operations = new[] { new { op = "setGravity", value = 6 } } }));
            var report = Json(plan.Report);
            var colors = report.GetProperty("dynamicVertexColors");
            Assert.Equal(report.GetProperty("outputSha256").GetString(), colors.GetProperty("proposedOutputSha256").GetString());
            Assert.Equal(1, colors.GetProperty("retailUnknown").GetInt32());
            Assert.Equal(5, colors.GetProperty("retailCompacted").GetInt32());
            Assert.Equal(bytes, File.ReadAllBytes(input)); Assert.Single(Directory.GetFiles(directory));
            foreach (string request in new[] { """{"schemaVersion":1,"limit":201}""", """{"schemaVersion":1,"offset":-1}""",
                """{"schemaVersion":1,"sector":-1}""", """{"schemaVersion":1,"prepare":true}""" })
                Assert.Throws<AgentRequestException>(() => AgentWorkspace.DiagnoseVertexColors(input, request));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
    private static Level Room() => LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
    private static string RoomText() => JklWriter.Build(new JklDocument(Room()));
    private static string Hash(string text) => AgentWorkspace.Hash(Encoding.UTF8.GetBytes(text));
    private static string ReplaceLighting(string text, int surface, string corner)
        => ReplaceLightingCorners(text, surface, Enumerable.Repeat(corner, 4).ToArray());

    private static string ReplaceLightingCorners(string text, int surface, string[] corners)
    {
        string[] rows = text.Replace("\r\n", "\n").Split('\n');
        int table = Array.FindIndex(rows, s => s.StartsWith("WORLD SURFACES ", StringComparison.OrdinalIgnoreCase));
        Assert.True(table >= 0);
        int index = table + 1;
        while (index < rows.Length && (string.IsNullOrWhiteSpace(rows[index]) || rows[index].TrimStart().StartsWith('#'))) index++;
        index += surface;
        var tokens = JklReader.Tokens(JklReader.StripIndex(rows[index]));
        int count = JklReader.ParseInt(tokens[8]);
        Assert.Equal(count, corners.Length);
        rows[index] = $"{surface}: " + string.Join(' ', tokens.Take(9 + count)) + " " + string.Join(' ', corners);
        return string.Join('\n', rows);
    }
}

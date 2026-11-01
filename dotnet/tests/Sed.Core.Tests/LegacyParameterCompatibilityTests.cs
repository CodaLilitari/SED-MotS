using Sed.Core.Model;
using Sed.Formats.Jed;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class LegacyParameterCompatibilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-legacy-parameters-" + Guid.NewGuid().ToString("N"));
    private const string FirstFrame = "(0/0/0:0/0/0)";
    private const string FinalFrame = "(1/2/3:0/90/0";
    private const string TrailingFrame = "(1/2/3:0/90/0)7/0)";

    public LegacyParameterCompatibilityTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NewParameterAfterLegacyFinalFrameIsRefusedWithoutChangingFilesOrModel(bool native, bool template)
    {
        var document = Import(native, template, FinalFrame);
        var (values, records) = Parameters(document, template);
        string validBefore = JklWriter.Build(document);
        string[] sourceBefore = document.SourceLines.ToArray();
        var recordsBefore = records.ToArray();
        string[] warningsBefore = document.PreservationWarnings.ToArray();
        values.Add("timer", "5");
        var valuesBefore = values.ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains(template ? "Template 'synthetic'" : "Thing 0", error.Message);
        Assert.Contains("legacy final frame", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timer", error.Message);
        Assert.Contains("preserv", error.Message, StringComparison.OrdinalIgnoreCase);

        string path = Path.Combine(_directory, "retained.jkl");
        byte[] destinationBefore = [0xef, 0xbb, 0xbf, 0x23, 0x20, 0xe9, 0x0d, 0x0a];
        File.WriteAllBytes(path, destinationBefore);
        Assert.Throws<InvalidOperationException>(() => JklWriter.Save(document, path));
        Assert.Equal(destinationBefore, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(_directory));
        Assert.Equal(sourceBefore, document.SourceLines);
        Assert.Equal(recordsBefore, records);
        Assert.Equal(valuesBefore, values);
        Assert.Equal(warningsBefore, document.PreservationWarnings);

        // Removing the incompatible addition restores the original usable document.
        values.Remove("timer");
        Assert.Equal(validBefore, JklWriter.Build(document));
        JklWriter.Save(document, path);
        var reopened = JklParser.ParseDocument(File.ReadAllText(path));
        Assert.Equal(FinalFrame, Parameters(reopened, template).Values["frame"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExistingParameterEditsKeepBothFramesAndTheirOriginalOrder(bool native, bool template)
    {
        var document = Import(native, template, FinalFrame);
        if (!native) Assert.Equal(JklSource(template, FinalFrame), JklWriter.Build(document));
        var (values, records) = Parameters(document, template);
        var recordsBefore = records.ToArray();
        values["health"] = "76";
        string path = Path.Combine(_directory, "edited.jkl");
        JklWriter.Save(document, path);

        var reopened = JklParser.ParseDocument(File.ReadAllText(path));
        var (savedValues, savedRecords) = Parameters(reopened, template);
        Assert.Equal("76", savedValues["health"]);
        Assert.Equal(new[] { "numframes", "frame", "health", "frame" }, savedRecords.Select(p => p.Key));
        Assert.Equal(new[] { FirstFrame, FinalFrame }, savedRecords.Where(p => p.Key == "frame").Select(p => p.Value));
        Assert.Single(reopened.PreservationWarnings, w => w.Contains("legacy final frame"));
        Assert.Equal(recordsBefore, records); // Saving does not rewrite the retained imported records.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TrailingParenthesisCompatibilityStillAllowsNewParameters(bool native, bool template)
    {
        var document = Import(native, template, TrailingFrame);
        if (!native) Assert.Equal(JklSource(template, TrailingFrame), JklWriter.Build(document));
        Parameters(document, template).Values.Add("timer", "5");
        var reopened = JklParser.ParseDocument(JklWriter.Build(document));
        var (values, records) = Parameters(reopened, template);
        Assert.Equal("5", values["timer"]);
        Assert.Equal(new[] { FirstFrame, TrailingFrame }, records.Where(p => p.Key == "frame").Select(p => p.Value));
        Assert.Equal("timer", records[^1].Key);
        Assert.Single(reopened.PreservationWarnings, w => w.Contains("unmatched ')'"));
    }

    [Theory]
    [InlineData("frame=(1/2/3:0/90")]
    [InlineData("frame=(1/2/3:0/90/0 timer=5")]
    [InlineData("frame=(1/2/3:0/90/NaN")]
    [InlineData("orient=(1/2/3:0/90/0")]
    [InlineData("frame=(1/2/3:0/90/0)7)(")]
    [InlineData("frame=(1/2/3:0/90/Infinity)7)")]
    public void OtherMalformedParametersRemainRefused(string source) =>
        Assert.Throws<FormatException>(() => JklReader.ParameterTokens(source));

    private static (Dictionary<string, string> Values, List<KeyValuePair<string, string>> Records)
        Parameters(JklDocument document, bool template) => template
            ? (document.Level.Templates["synthetic"].Values, document.Level.Templates["synthetic"].SourceParameters)
            : (document.Level.Things[0].Values, document.Level.Things[0].SourceParameters);

    private static string ParameterText(string finalFrame) =>
        $"numframes=2 frame={FirstFrame} health=75 frame={finalFrame}";

    private static JklDocument Import(bool native, bool template, string finalFrame)
    {
        if (!native) return JklParser.ParseDocument(JklSource(template, finalFrame));
        var templates = JedImporter.LoadTemplateTexts([
            "synthetic none " + (template ? ParameterText(finalFrame) : "move=path")]);
        return JedImporter.ImportText($"""
            JED 0.95
            SEC: HEADER
            HDR 1 4 15 2 3 dflt.cmp 320
            HOR 100 768 0 0
            CEIL 0 0
            MIP 1 2 3 4
            LOD 1 2 3 4
            END
            SEC: THINGS
            THS 1
            synthetic 0 0 0 0 0 0 -1 0
            {(template ? "health=75" : ParameterText(finalFrame))}
            END
            """, templates).Document;
    }

    private static string JklSource(bool template, string finalFrame) => $"""
        # Original synthetic compatibility fixture; no community-level payload.
        SECTION: HEADER
        VERSION 1
        END
        SECTION: TEMPLATES
        World templates 1
        synthetic none {(template ? ParameterText(finalFrame) : "move=path")}
        END
        SECTION: THINGS
        World things 8
        0: synthetic Synthetic 0 0 0 0 0 0 -1 {(template ? "health=75" : ParameterText(finalFrame))}
        END
        """;

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

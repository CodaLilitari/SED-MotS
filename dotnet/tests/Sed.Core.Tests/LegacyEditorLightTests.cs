using Sed.Core.Math;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class LegacyEditorLightTests
{
    [Fact]
    public void SixFieldJedLightsSurviveAnEditAndSaveBesideModernRows()
    {
        const string text = """
            SECTION: HEADER
            VERSION 1
            END
            SECTION: LIGHTS
            Editor lights 3
            0: 4 1 2 3 0.375 9.5
            1: 0x1 2 4 5 6 7.25 0.625
            2: 0x0 3 7 8 9 4.75 0.125 0.2 0.4 0.6
            END
            """;
        var doc = JklParser.ParseDocument(text);
        Assert.Equal(3, doc.Level.Lights.Count);
        var first = doc.Level.Lights[0];
        Assert.Equal(4, first.Layer); Assert.Equal(0, first.Flags);
        Assert.Equal(new Vec3(1, 2, 3), first.Position);
        Assert.Equal(.375, first.Intensity); Assert.Equal(9.5, first.Range);
        Assert.Equal(7.25, doc.Level.Lights[1].Range);
        Assert.Equal(.625, doc.Level.Lights[1].Intensity);
        doc.Level.Kind = Sed.Core.Model.ProjectType.MysteriesOfTheSith;
        first.Range = 8.75; // Forces the LIGHTS section rewrite that used to drop the legacy row.
        var after = JklParser.Parse(JklWriter.Build(doc));
        Assert.Equal(3, after.Lights.Count);
        Assert.Equal(.375, after.Lights[0].Intensity); Assert.Equal(8.75, after.Lights[0].Range);
        Assert.Equal(new ColorF(.2f, .4f, .6f), after.Lights[2].Color);
    }

    [Theory]
    [InlineData("0: 1 2 3 4 5")]
    [InlineData("0: 0 1 2 3 4 5 6 7")]
    [InlineData("")]
    public void UnsupportedOrTruncatedRowsCannotSilentlyDisappear(string row)
        => Assert.Throws<FormatException>(() => JklParser.Parse($"SECTION: LIGHTS\nEditor lights 1\n{row}\nEND\n"));
}

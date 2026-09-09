using System.Globalization;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public class JklNumericRoundTripTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void SignedZeroUvComponentsSurviveRegeneration(bool mots, bool editSource, bool reverse)
    {
        string[] uv = ["0 0", "-.00 0", "0 -.00", "-.00 -.00"];
        if (reverse) Array.Reverse(uv);
        var doc = JklParser.ParseDocument(Source(mots, uv));
        Assert.Equal(mots ? ProjectType.MysteriesOfTheSith : ProjectType.JediKnight, doc.Level.Kind);
        AssertUvBits(uv, doc.Level);

        string output = Regenerate(doc, editSource);

        // All four sign combinations must remain separate even though ordinary
        // floating-point equality considers their coordinates equal.
        Assert.Contains("World texture vertices 4", output);
        AssertUvBits(uv, JklParser.Parse(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdenticalUvBitPatternsStillShareOneTextureVertex(bool mots)
    {
        string[] uv = ["-.00 1.25", "-0 1.25", "-0.000 1.250", "-0e0 1.25"];
        var doc = JklParser.ParseDocument(Source(mots, uv));

        string output = Regenerate(doc, editSource: false);

        Assert.Contains("World texture vertices 1", output);
        AssertUvBits(uv, JklParser.Parse(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FiniteUvPrecisionSurvivesForcedRegeneration(bool mots)
    {
        string[] uv = ["1.0000000000000002 -1.0000000000000002", "1 1",
            "1.2345678901234567e-20 -1.2345678901234567e-20", "123456789.12345679 -123456789.12345679"];
        var doc = JklParser.ParseDocument(Source(mots, uv));

        string output = Regenerate(doc, editSource: false);

        Assert.Contains("World texture vertices 4", output);
        AssertUvBits(uv, JklParser.Parse(output));
    }

    private static string Regenerate(JklDocument doc, bool editSource)
    {
        if (editSource)
        {
            // A normal geometry edit must rewrite GEORESOURCE while retaining
            // the original document and its unchanged-section baselines.
            doc.Level.Sectors[0].Vertices[0].Position += new Sed.Core.Math.Vec3(0, 0, 1);
            return JklWriter.Build(doc);
        }
        return JklWriter.Build(new JklDocument(doc.Level));
    }

    private static void AssertUvBits(string[] expected, Level level)
    {
        var corners = Assert.Single(Assert.Single(level.Sectors).Surfaces).Corners;
        Assert.Equal(expected.Length, corners.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            var parts = expected[i].Split(' ');
            Assert.Equal(Bits(parts[0]), BitConverter.DoubleToInt64Bits(corners[i].Uv.U));
            Assert.Equal(Bits(parts[1]), BitConverter.DoubleToInt64Bits(corners[i].Uv.V));
        }
    }

    private static long Bits(string value) => BitConverter.DoubleToInt64Bits(
        double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));

    // Authored synthetic quad; no community level contents are copied here.
    private static string Source(bool mots, string[] uv) => $$"""
        SECTION: HEADER
        VERSION 1
        END
        SECTION: GEORESOURCE
        World colormaps 1
        0: dflt.cmp
        World vertices 4
        0: 0 0 0
        1: 1 0 0
        2: 1 1 0
        3: 0 1 0
        World texture vertices 4
        0: {{uv[0]}}
        1: {{uv[1]}}
        2: {{uv[2]}}
        3: {{uv[3]}}
        World adjoins 0
        World surfaces 1
        0: -1 0 0 4 3 3 -1 0 4 0,0 1,1 2,2 3,3 {{string.Join(' ', Enumerable.Repeat("0.5", mots ? 16 : 4))}}
        0: 0 0 1
        END
        SECTION: SECTORS
        World sectors 1
        SECTOR 0
        SURFACES 0 1
        END
        """;
}

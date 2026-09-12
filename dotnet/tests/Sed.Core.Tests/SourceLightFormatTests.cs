using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jed;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

public sealed class SourceLightFormatTests
{
    private const string MetadataName = "SECTION: SED_SOURCE_LIGHTS";

    private static string Native(string row, bool mots) => $$"""
        SED 0.3.0
        {{(mots ? "SEC: MOTS\nEND" : "")}}
        SEC: HEADER
        HDR 1 4 15 2 3 dflt.cmp 320
        HOR 100 768 0 0
        CEIL 0 0
        MIP 1 2 3 4
        LOD 0.1 0.2 0.3 0.4
        END
        SEC: LIGHTS
        LTS 1
        {{row}}
        END
        """;

    private static JklDocument Document(bool mots = true)
    {
        var level = LevelFactory.CreateUntexturedRoom(mots ? ProjectType.MysteriesOfTheSith : ProjectType.JediKnight);
        var light = level.NewLight();
        light.Position = new(1.2345678901234567, 2.3456789012345678, -3.4567890123456789);
        light.Range = 9.876543210987654;
        light.Intensity = .12345678901234568;
        light.Color = mots ? new(.2f, .4f, .6f) : ColorF.White;
        return new(level);
    }

    private static string MetadataBlock(string text)
    {
        int start = text.IndexOf(MetadataName, StringComparison.Ordinal);
        int end = text.IndexOf("\nEND", start, StringComparison.Ordinal);
        return text[start..(end + 4)];
    }

    [Fact]
    public void CanonicalChecksumMatchesAnIndependentFixedPrecisionVector()
    {
        // Literal canonical bytes and digest were computed independently with
        // Python 3 hashlib, not by the production canonicalization code.
        const string canonical = "sed-source-light-base-v1\n1\n" +
            "2147483649 2 1 1.0000000000000002 -0 5.000000000000001 0.1 " +
            "0.10000000149011612 0.10000000894069672 -0\n";
        const string expected = "63d045cfe6c98062d8cbd2daf5a37676af67ddab6698f2d5186f76bbad9bc1d0";
        Assert.Equal(129, Encoding.UTF8.GetByteCount(canonical));
        Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));

        var doc = Document();
        var light = doc.Level.Lights[0];
        light.Flags = 2147483649; light.Layer = 2;
        light.Position = new(1, System.Math.BitIncrement(1), BitConverter.Int64BitsToDouble(long.MinValue));
        light.Range = System.Math.BitIncrement(5); light.Intensity = .1;
        light.Color = new(.1f, BitConverter.Int32BitsToSingle(0x3dccccce), BitConverter.Int32BitsToSingle(int.MinValue));
        light.RgbIntensity = System.Math.BitIncrement(.1);
        light.LegacyRgbRange = BitConverter.Int64BitsToDouble(long.MinValue);
        string saved = JklWriter.Build(doc);
        Assert.Contains("LIGHTS_SHA256 " + expected, saved);
        Assert.Equal(light.RgbIntensity, Assert.Single(JklParser.Parse(saved).Lights).RgbIntensity);

        string changed = saved.Replace("1.0000000000000002", "1", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(changed));
    }

    [Fact]
    public void NativePrecisionAndSignedZeroSurviveJklReopenAtTheirStoredBitWidths()
    {
        var doc = JedImporter.ImportText(Native(
            "1 1.0000000000000002 -0 2 0.1 5.000000000000001 80000001 -0 0.10000000000000002 0.1 0.10000001 -0", true)).Document;
        var light = doc.Level.Lights[0];
        Assert.Equal(System.Math.BitIncrement(1), light.Position.Y);
        Assert.Equal(System.Math.BitIncrement(5), light.Range);
        Assert.Equal(System.Math.BitIncrement(.1), light.RgbIntensity);
        Assert.Equal(0x3dcccccd, BitConverter.SingleToInt32Bits(light.Color.R));
        Assert.Equal(0x3dccccce, BitConverter.SingleToInt32Bits(light.Color.G));
        var after = Assert.Single(JklParser.Parse(JklWriter.Build(doc)).Lights);
        Assert.Equal(light.Position, after.Position);
        Assert.Equal(light.Intensity, after.Intensity);
        Assert.Equal(light.Range, after.Range);
        Assert.Equal(light.RgbIntensity, after.RgbIntensity);
        Assert.Equal(light.Color, after.Color);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(after.Position.Z));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(after.LegacyRgbRange!.Value));
        Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(after.Color.B));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyNativeChannelsSurviveConversionIndependentlyForBothGames(bool mots)
    {
        var imported = JedImporter.ImportText(Native("1 2 3 0 2.25 5.5 1 9.75 0 0.2 0.4 0.6", mots));
        var light = Assert.Single(imported.Document.Level.Lights);
        Assert.Equal(2.25, light.Intensity);
        Assert.Equal(0, light.RgbIntensity);
        Assert.Equal(0, light.EffectiveRgbIntensity);
        Assert.Equal(5.5, light.Range);
        Assert.Equal(9.75, light.LegacyRgbRange);
        Assert.Equal(new ColorF(.2f, .4f, .6f), light.Color);
        Assert.DoesNotContain(imported.Warnings, warning => warning.Contains("not represented", StringComparison.Ordinal));
        string saved = JklWriter.Build(imported.Document);
        var reopened = Assert.Single(JklParser.Parse(saved).Lights);
        Assert.Equal(light.Intensity, reopened.Intensity);
        Assert.Equal(light.RgbIntensity, reopened.RgbIntensity);
        Assert.Equal(light.LegacyRgbRange, reopened.LegacyRgbRange);
        Assert.Equal(light.Color, reopened.Color);
        Assert.Contains(MetadataName, saved);
        string row = saved.Split('\n').First(line => line.StartsWith("0: 0x", StringComparison.Ordinal));
        Assert.Equal(mots ? 10 : 7, JklReader.Tokens(JklReader.StripIndex(row)).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurrentNativeRowsKeepInheritedIntensityAndAbsentRangeMetadata(bool mots)
    {
        var imported = JedImporter.ImportText(Native("1 2 3 0 2.25 5.5 1 0.2 0.4 0.6", mots));
        var light = Assert.Single(imported.Document.Level.Lights);
        Assert.Null(light.RgbIntensity);
        Assert.Null(light.LegacyRgbRange);
        Assert.Equal(2.25, light.EffectiveRgbIntensity);
        var after = Assert.Single(JklParser.Parse(JklWriter.Build(imported.Document)).Lights);
        Assert.Null(after.RgbIntensity);
        Assert.Null(after.LegacyRgbRange);
        Assert.Equal(light.Color, after.Color);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 0.0)]
    [InlineData(.12345678901234568, 9.876543210987654)]
    [InlineData(1.2345678901234567, 2.3456789012345678)]
    public void MetadataPreservesExplicitZeroEqualAndInheritedStates(double? rgb, double? legacyRange)
    {
        var doc = Document();
        var before = doc.Level.Lights[0];
        before.RgbIntensity = rgb; before.LegacyRgbRange = legacyRange;
        string saved = JklWriter.Build(doc);
        var after = Assert.Single(JklParser.Parse(saved).Lights);
        Assert.Equal(before.Intensity, after.Intensity);
        Assert.Equal(before.Range, after.Range);
        Assert.Equal(before.Position, after.Position);
        Assert.Equal(before.Color, after.Color);
        Assert.Equal(rgb, after.RgbIntensity);
        Assert.Equal(legacyRange, after.LegacyRgbRange);
        Assert.Equal(rgb.HasValue || legacyRange.HasValue, saved.Contains(MetadataName, StringComparison.Ordinal));
    }

    [Fact]
    public void InactiveJkColorIsPreservedWithoutExtendingOrdinaryLightRows()
    {
        const string legacy = "SECTION: LIGHTS\nEditor lights 1\n0: 0x0 0 1 2 3 4 5 0.2 0.4 0.6\nEND\n";
        var doc = JklParser.ParseDocument(legacy);
        var expected = doc.Level.Lights[0].Color;
        string saved = JklWriter.Build(doc);
        Assert.Contains(MetadataName, saved);
        Assert.Equal(expected, Assert.Single(JklParser.Parse(saved).Lights).Color);
    }

    [Fact]
    public void UntouchedUnifiedLightDocumentRetainsExactSourceAndAddsNoMetadata()
    {
        const string source = "# original spacing\nSECTION: LIGHTS\nEditor lights 1\n0: 0x1 0 1.00 2.0 3.0 5.00 0.25\nEND\n";
        string saved = JklWriter.Build(JklParser.ParseDocument(source));
        Assert.Equal(source, saved);
        Assert.DoesNotContain(MetadataName, saved);
    }

    [Fact]
    public void MetadataOnlyEditsChangeOutputAndDocumentRevisionEvenWhenEffectiveIntensityDoesNot()
    {
        var doc = Document();
        string before = JklWriter.Build(doc), revision = ResourceValidatedExport.DocumentRevision(doc);
        doc.Level.Lights[0].RgbIntensity = doc.Level.Lights[0].Intensity;
        string explicitEqual = JklWriter.Build(doc);
        Assert.NotEqual(before, explicitEqual);
        Assert.NotEqual(revision, ResourceValidatedExport.DocumentRevision(doc));
        var parsed = JklParser.ParseDocument(explicitEqual);
        parsed.Level.Lights[0].RgbIntensity = null;
        parsed.Level.Lights[0].LegacyRgbRange = 0;
        var after = Assert.Single(JklParser.Parse(JklWriter.Build(parsed)).Lights);
        Assert.Null(after.RgbIntensity);
        Assert.Equal(0, after.LegacyRgbRange);
        Assert.Equal(after.Intensity, after.EffectiveRgbIntensity);
    }

    [Fact]
    public void ClearingFinalOverridesCannotLeaveStaleMetadataBehind()
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2; doc.Level.Lights[0].LegacyRgbRange = 9;
        var parsed = JklParser.ParseDocument(JklWriter.Build(doc));
        parsed.Level.Lights[0].RgbIntensity = null; parsed.Level.Lights[0].LegacyRgbRange = null;
        var after = Assert.Single(JklParser.Parse(JklWriter.Build(parsed)).Lights);
        Assert.Null(after.RgbIntensity); Assert.Null(after.LegacyRgbRange);
        parsed.Level.Lights.Clear();
        Assert.Empty(JklParser.Parse(JklWriter.Build(parsed)).Lights);
    }

    [Fact]
    public void MetadataSectionMayPrecedeLightsAndUnchangedRoundTripPreservesItsText()
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2;
        string saved = JklWriter.Build(doc), metadata = MetadataBlock(saved);
        string reordered = metadata + "\n" + saved.Replace(metadata, "", StringComparison.Ordinal);
        var parsed = JklParser.ParseDocument(reordered);
        Assert.Equal(2, Assert.Single(parsed.Level.Lights).RgbIntensity);
        Assert.Equal(reordered, JklWriter.Build(parsed));
    }

    [Fact]
    public void OtherEditorChangesToOrdinaryLightRowsInvalidateMetadata()
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2;
        string saved = JklWriter.Build(doc);
        string changed = saved.Replace("0: 0x0 0 ", "0: 0x1 0 ", StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(changed));
    }

    [Fact]
    public void MetadataCannotOverrideColorAlreadySuppliedByTheOrdinaryRgbRow()
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2;
        string saved = JklWriter.Build(doc), block = MetadataBlock(saved);
        string[] rows = block.Split('\n');
        var fields = JklReader.Tokens(rows[4]); fields[3] = "0.9";
        rows[4] = string.Join(' ', fields);
        string malformed = saved.Replace(block, string.Join('\n', rows), StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(malformed));
        // The retained-source guard parses only LIGHTS and metadata sections;
        // validation must depend on the actual RGB row, not inferred game kind.
        var retained = JklParser.ParseDocument(saved);
        int start = Array.FindIndex(retained.SourceLines, line => line == MetadataName);
        retained.SourceLines[start + 4] = rows[4];
        Assert.Throws<FormatException>(() => JklWriter.Build(retained));
    }

    [Fact]
    public void RedundantMetadataColorMustPreserveTheOrdinaryRowsZeroSign()
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2;
        doc.Level.Lights[0].Color = new(BitConverter.Int32BitsToSingle(int.MinValue), .4f, .6f);
        string saved = JklWriter.Build(doc), block = MetadataBlock(saved);
        string[] rows = block.Split('\n');
        var fields = JklReader.Tokens(rows[4]); fields[3] = "0";
        rows[4] = string.Join(' ', fields);
        string malformed = saved.Replace(block, string.Join('\n', rows), StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(malformed));
    }

    [Theory]
    [InlineData("1: 0x0 0 1 2 3 4 5\nEND\n", false)]
    [InlineData("1: 0x0 0 1 2 3 4 5\nEND\n", true)]
    [InlineData("UNKNOWN 1\nEND\n", false)]
    [InlineData("END\n1: 0x0 0 1 2 3 4 5\n", false)]
    [InlineData("END\n1: 0x0 0 1 2 3 4 5\n", true)]
    [InlineData("END\nEND\n", false)]
    public void ExtraOrdinaryLightContentCannotBeSilentlySkipped(string tail, bool withMetadata)
    {
        var doc = Document();
        if (withMetadata) doc.Level.Lights[0].RgbIntensity = 2;
        string saved = JklWriter.Build(doc);
        int start = saved.IndexOf("SECTION: LIGHTS", StringComparison.Ordinal);
        int end = saved.IndexOf("\nend", start, StringComparison.Ordinal);
        string malformed = saved[..(end + 1)] + tail + saved[(end + 4)..];
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(malformed));
    }

    [Fact]
    public void ZeroDeclaredLightsCannotHideAnExtraRow()
    {
        const string malformed = "SECTION: LIGHTS\nEditor lights 0\n0: 0x0 0 1 2 3 4 5\nEND\n";
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(malformed));
    }

    [Theory]
    [InlineData("0 1 2 3 5 4", false, false)]
    [InlineData("0x0 0 1 2 3 4 5", false, true)]
    [InlineData("0x0 0 1 2 3 4 5 0.2 0.4 0.6", false, true)]
    [InlineData("0x0 0 1 2 3 4 5", true, false)]
    [InlineData("0x0 0 1 2 3 4 5", true, true)]
    public void LegacyLightsMayEndAtEndTokenNextSectionOrEof(string row, bool explicitEnd, bool nextSection)
    {
        string source = "SECTION: LIGHTS\nEditor lights 1\n0: " + row + "\n# retained comment\n\n" +
            (explicitEnd ? "END\n# after end\n" : "") +
            (nextSection ? "SECTION: HEADER\nVERSION 1\nWORLD GRAVITY 7\nEND\n" : "");
        var doc = JklParser.ParseDocument(source);
        Assert.Single(doc.Level.Lights);
        if (nextSection) Assert.Equal(7, doc.Level.Header.Gravity);
        Assert.Single(JklParser.Parse(JklWriter.Build(doc)).Lights);
    }

    [Theory]
    [InlineData("\nUNSUPPORTED 1\n")]
    [InlineData("\n0: 1 none 1 1 1\n")]
    [InlineData("\nEND\n")]
    public void TrailingMetadataRowsAfterEndCannotBeSilentlySkipped(string trailing)
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2;
        string saved = JklWriter.Build(doc), block = MetadataBlock(saved);
        string malformed = saved.Replace(block, block + trailing, StringComparison.Ordinal);
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(malformed));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("count")]
    [InlineData("index")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("unknown")]
    [InlineData("nonfinite")]
    [InlineData("hash")]
    public void UnsupportedOrMalformedMetadataFailsBeforeAnyDocumentIsReturned(string kind)
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2;
        string saved = JklWriter.Build(doc), block = MetadataBlock(saved);
        string replacement = kind switch
        {
            "version" => block.Replace("VERSION 1", "VERSION 2", StringComparison.Ordinal),
            "count" => block.Replace("SOURCE_LIGHTS 1", "SOURCE_LIGHTS 0", StringComparison.Ordinal),
            "index" => block.Replace("0: 2 none", "1: 2 none", StringComparison.Ordinal),
            "duplicate" => block + "\n" + block,
            "extra" => block.Replace("\nEND", "\nEXTRA 1\nEND", StringComparison.Ordinal),
            "unknown" => block.Replace("\nSOURCE_LIGHTS", "\nUNKNOWN_LIGHTS", StringComparison.Ordinal),
            "nonfinite" => block.Replace("0: 2 none", "0: NaN none", StringComparison.Ordinal),
            "hash" => block.Replace("LIGHTS_SHA256 ", "LIGHTS_SHA256 x", StringComparison.Ordinal),
            _ => throw new ArgumentException(kind)
        };
        string malformed = saved.Replace(block, replacement, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => JklParser.ParseDocument(malformed));
    }

    [Fact]
    public void RawCopyCannotBypassValidationAfterSourceLinesAreChanged()
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2;
        var parsed = JklParser.ParseDocument(JklWriter.Build(doc));
        int start = Array.FindIndex(parsed.SourceLines, line => line == MetadataName);
        parsed.SourceLines[start + 1] = "VERSION 99";
        Assert.Throws<NotSupportedException>(() => JklWriter.Build(parsed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidRawMetadataMutationOrRemovalCannotDisagreeWithTheCapturedModel(bool remove)
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = 2;
        var parsed = JklParser.ParseDocument(JklWriter.Build(doc));
        int start = Array.FindIndex(parsed.SourceLines, line => line == MetadataName);
        if (remove)
            for (int i = start; i < parsed.SourceLines.Length; i++) parsed.SourceLines[i] = "";
        else parsed.SourceLines[start + 4] = parsed.SourceLines[start + 4].Replace("0: 2 none", "0: 9 none", StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => JklWriter.Build(parsed));
    }

    [Fact]
    public void CultureDoesNotChangeSourceLightMetadataOrItsChecksum()
    {
        var doc = Document(); doc.Level.Lights[0].RgbIntensity = .12345678901234568;
        string invariant = JklWriter.Build(doc);
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(invariant, JklWriter.Build(doc));
            Assert.Equal(doc.Level.Lights[0].RgbIntensity, Assert.Single(JklParser.Parse(invariant).Lights).RgbIntensity);
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }
}

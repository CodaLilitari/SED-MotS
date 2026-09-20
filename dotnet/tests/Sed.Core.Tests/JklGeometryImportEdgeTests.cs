using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Jkl;
using Xunit;

namespace Sed.Core.Tests;

public class JklGeometryImportEdgeTests
{
    [Fact]
    public void DuplicateCaseInsensitiveColormapNamesKeepTheSelectedNumericSlot()
    {
        string source = Room().Replace("World colormaps 1", "World colormaps 2")
            .Replace("0:\tdflt.cmp", "0:\tdflt.cmp\n1:\tDFLT.CMP")
            .Replace("COLORMAP\t0", "COLORMAP\t1");
        var document = JklParser.ParseDocument(source);
        Assert.Equal(2, document.Level.ColorMaps.Count);
        Assert.Equal("DFLT.CMP", document.Level.Sectors[0].ColorMap);
        Assert.DoesNotContain(ConsistencyChecker.Check(document.Level), issue => issue.Severity == IssueSeverity.Error);
        document.Level.Sectors.Add(SectorFactory.CreateBox(document.Level, new Vec3(5, 0, 0), 1, "original-synthetic.mat", 0));
        string output = JklWriter.Build(document);
        Assert.Equal("COLORMAP\t1", output.Split('\n').First(line => line.StartsWith("COLORMAP\t", StringComparison.Ordinal)));
        Assert.Equal("DFLT.CMP", JklParser.Parse(output).Sectors[0].ColorMap);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    public void InvalidImportedColormapCanBeRecoveredButCannotBecomeSlotZeroOnRewrite(int colormapIndex)
    {
        string source = Room().Replace("COLORMAP\t0", $"COLORMAP\t{colormapIndex}");
        var document = JklParser.ParseDocument(source);
        Assert.Equal(source, JklWriter.Build(document));
        document.Level.Header.Gravity += 1;
        Assert.Contains($"COLORMAP\t{colormapIndex}", JklWriter.Build(document));
        document.Level.Sectors[0].Ambient = new ColorF(.25f, .25f, .25f);
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains("colormap", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(colormapIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);
    }

    [Fact]
    public void InvalidPositiveTextureVertexCanBeRecoveredButCannotBecomeZeroUvOnRewrite()
    {
        string source = ChangeSurfaceToken(Room(), 0, 10, token => token.Split(',')[0] + ",99");
        var document = JklParser.ParseDocument(source);
        Assert.Equal(source, JklWriter.Build(document));
        document.Level.Header.Gravity += 1;
        Assert.Contains(",99", JklWriter.Build(document));
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .25f;
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains("surface 0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("corner 0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("99", error.Message);
    }

    [Fact]
    public void NegativeTextureVertexWithNamedMaterialDoesNotClaimAnEngineSentinel()
    {
        string source = ChangeSurfaceToken(Room(), 0, 10, token => token.Split(',')[0] + ",-1");
        var document = JklParser.ParseDocument(source);
        Assert.Equal(source, JklWriter.Build(document));
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .25f;
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains("surface 0 corner 0", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("texture vertex -1", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(99)]
    public void ExplicitlyMateriallessSurfaceKeepsEngineIgnoredTextureVertexCompatibility(int textureVertexIndex)
    {
        string source = ChangeSurfaceToken(Room(), 0, 1, _ => "-1");
        source = ChangeSurfaceToken(source, 0, 10, token => token.Split(',')[0] + "," + textureVertexIndex);
        var document = JklParser.ParseDocument(source);
        Assert.Equal(-1, document.Level.Sectors[0].Surfaces[0].MaterialIndex);
        Assert.Equal(string.Empty, document.Level.Sectors[0].Surfaces[0].Material);
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .25f;
        var reopened = JklParser.ParseDocument(JklWriter.Build(document));
        Assert.Equal(-1, reopened.Level.Sectors[0].Surfaces[0].MaterialIndex);
        Assert.Equal(.25f, reopened.Level.Sectors[0].Surfaces[0].ExtraLightIntensity);
    }

    [Fact]
    public void EmptyColormapTableWithSlotZeroRemainsAnEditableScaffold()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.Sectors.Add(SectorFactory.CreateBox(level, Vec3.Zero, 1, "original-synthetic.mat", 0));
        string source = JklWriter.Build(new JklDocument(level));
        Assert.Contains("World colormaps 0", source);
        Assert.Contains("COLORMAP\t0", source);
        var document = JklParser.ParseDocument(source);
        document.Level.Sectors.Add(SectorFactory.CreateBox(document.Level, new Vec3(5, 0, 0), 1, "original-synthetic.mat", 0));
        var reopened = JklParser.ParseDocument(JklWriter.Build(document));
        Assert.Empty(reopened.Level.ColorMaps);
        Assert.Equal(2, reopened.Level.Sectors.Count);
    }

    [Fact]
    public void MateriallessMissingTextureVertexCannotSilentlyBecomeTextured()
    {
        string source = ChangeSurfaceToken(Room(), 0, 1, _ => "-1");
        source = ChangeSurfaceToken(source, 0, 10, token => token.Split(',')[0] + ",99");
        var document = JklParser.ParseDocument(source);
        var surface = document.Level.Sectors[0].Surfaces[0];
        surface.Material = "original-synthetic.mat";
        surface.MaterialIndex = 0;
        Assert.Contains("texture vertex 99", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Theory]
    [InlineData("GEORESOURCE")]
    [InlineData("MATERIALS")]
    [InlineData("SECTORS")]
    public void UnmodeledTailRowsSurviveRecoveryAndRefuseRewritingTheirSection(string section)
    {
        const string extension = "ORIGINAL_GEOMETRY_EXTENSION 42";
        var lines = Room().Split('\n').ToList();
        int start = lines.FindIndex(line => line.Equals("SECTION: " + section, StringComparison.OrdinalIgnoreCase));
        Assert.True(start >= 0);
        int end = lines.FindIndex(start + 1, line => line.Equals("END", StringComparison.OrdinalIgnoreCase));
        Assert.True(end > start);
        lines.Insert(end, extension);
        string source = string.Join('\n', lines);
        var document = JklParser.ParseDocument(source);
        Assert.Equal(source, JklWriter.Build(document));
        document.Level.Header.Gravity += 1;
        Assert.Contains(extension, JklWriter.Build(document));
        switch (section)
        {
            case "GEORESOURCE": document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .25f; break;
            case "SECTORS": document.Level.Sectors[0].Ambient = new ColorF(.25f, .25f, .25f); break;
            case "MATERIALS": document.Materials.Add("second-original-synthetic.mat"); break;
        }
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains("Cannot rewrite " + section, error.Message);
        Assert.Contains("unmodeled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string Room()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.ColorMaps.Add("dflt.cmp");
        var room = SectorFactory.CreateBox(level, Vec3.Zero, 1, "original-synthetic.mat", 0);
        room.ColorMap = "dflt.cmp";
        level.Sectors.Add(room);
        return JklWriter.Build(new JklDocument(level));
    }

    private static string ChangeSurfaceToken(string text, int surfaceIndex, int tokenIndex, Func<string, string> change)
    {
        var lines = text.Split('\n');
        int start = Array.FindIndex(lines, line => line.StartsWith("World surfaces ", StringComparison.Ordinal));
        Assert.True(start >= 0);
        var tokens = lines[start + surfaceIndex + 1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        tokens[tokenIndex] = change(tokens[tokenIndex]);
        lines[start + surfaceIndex + 1] = string.Join('\t', tokens);
        return string.Join('\n', lines);
    }
}

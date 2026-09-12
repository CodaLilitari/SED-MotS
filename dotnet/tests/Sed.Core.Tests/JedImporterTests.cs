using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jed;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public class JedImporterTests
{
    private const string Native = """
        SED 0.3.0
        SEC: MOTS
        END
        SEC: HEADER
        HDR 1 4 15 2 3 dflt.cmp 320.5
        HOR 100 768 0.1 0.2
        CEIL 0.3 0.4
        MIP 1 2 3 4
        LOD 0.1 0.2 0.3 0.4
        END
        SEC: GEO
        SECS 1
        dflt.cmp ambient.wav 0.25 4 0.125 0.25 0.3 0.4 0.5 1
        VXS 3
        0 0 0
        1 0 0
        0 1 0
        SURFS 1
        original.mat 4 0 4 3 3 0.5 3 -1 -1 0 1 1
        0 0 0 0.125 1 1 0 0.25 2 0 1 0.375 0.2 0.3 0.4 0.4 0.5 0.6 0.6 0.7 0.8
        END
        SEC: THINGS
        THS 1
        crate 1 2 3 4 5 6 0 1
        frame=(0/0/0:0/0/0) health=100 frame=(1/2/3:0/0/0)
        END
        SEC: LIGHTS
        LTS 2
        1 2 3 1 2 5 1 9 3 0.2 0.4 0.6
        4 5 6 0 4 8 0 0.3 0.5 0.7
        END
        SEC: COGS
        COGS 1
        level.cog floor:surface=0 player:thing=0 vector:vector=(1/2/3)
        END
        SEC: LAYERS
        LAYERS 2
        Main
        Second floor
        END
        SEC: ARCHLIGHTING
        Num ArchObjects 1
        archobject 0
        nummeshes: 1
        mesh 0
        numvertices: 1
        0: 0.125 0.25 0.5 0.75
        END
        SEC: ARCHLIGHTINGBINDINGS
        BINDINGS 1
        0 0 original.3do abcdef
        END
        SEC: LVIS
        11
        END
        SEC: HIERARCHYDEF
        NODES 0
        END
        """;

    private static Template BaseTemplate()
    {
        var template = new Template { Name = "crate", Parent = "none" };
        template.Values.Add("model3d", "original.3do");
        return template;
    }

    [Theory]
    [InlineData("SED 0.3.0")]
    [InlineData("JED 0.95")]
    [InlineData("NED 0.95")]
    public void ImportsObservedProjectSignaturesAndModeledFields(string signature)
    {
        var result = JedImporter.ImportText(Native.Replace("SED 0.3.0", signature), [BaseTemplate()]);
        var level = result.Document.Level;
        Assert.Equal(signature, result.Version);
        Assert.Equal(ProjectType.MysteriesOfTheSith, level.Kind);
        Assert.Equal(320.5, level.PixelsPerUnit);
        Assert.Equal(1, level.Sectors[0].Layer);
        Assert.Equal(.125f, level.Sectors[0].Surfaces[0].Corners[0].MonoIntensity);
        Assert.Equal(new ColorF(.2f, .3f, .4f), level.Sectors[0].Surfaces[0].Corners[0].Intensity);
        Assert.Equal(2, level.Lights[0].Intensity); // independent legacy white channel
        Assert.Equal(3, level.Lights[0].RgbIntensity);
        Assert.Equal(9, level.Lights[0].LegacyRgbRange); // preserved, not used by baking
        Assert.Equal(5, level.Lights[0].Range); // primary range
        Assert.Equal(4, level.Lights[1].Intensity); // current SED layout on next row
        Assert.Equal("Second floor", level.Layers[1]);
        Assert.Equal(new[] { "0", "0", "(1/2/3)" }, level.Cogs[0].Values);
        Assert.Equal(0, level.Things[0].ArchLightingIndex);
        Assert.Equal("abcdef", level.Things[0].ArchLightingSourceModelHash);
        Assert.Equal(257, level.ThingCapacity);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Independent white"));
        Assert.Contains(result.Warnings, w => w.Contains("SHA256"));
        Assert.Contains("ARCHLIGHTING", result.Coverage);
    }

    [Fact]
    public void JklConversionRetainsNativeModeledEngineFields()
    {
        var result = JedImporter.ImportText(Native, [BaseTemplate()]);
        var level = JklParser.Parse(JklWriter.Build(result.Document));
        Assert.Equal(.125f, level.Sectors[0].Surfaces[0].Corners[0].MonoIntensity);
        Assert.Equal(new ColorF(.2f, .3f, .4f), level.Sectors[0].Surfaces[0].Corners[0].Intensity);
        Assert.Equal("original.mat", level.Sectors[0].Surfaces[0].Material);
        Assert.Equal(0, level.Things[0].ArchLightingIndex);
        Assert.Equal(3, level.Things[0].SourceParameters.Count);
        Assert.Equal(new ArchitecturalLightVertex(.125, .25, .5, .75), level.ArchitecturalLighting[0].Meshes[0].Vertices[0]);
        Assert.Equal("Second floor", level.Layers[1]);
        Assert.Equal(1, level.Things[0].Layer);
    }

    [Fact]
    public void JkUsesPrimaryLegacyLightControl()
    {
        var source = Native[..Native.IndexOf("SEC: ARCHLIGHTING", StringComparison.Ordinal)]
            .Replace("SEC: MOTS\nEND\n", "");
        var level = JedImporter.ImportText(source, [BaseTemplate()]).Document.Level;
        Assert.Equal(ProjectType.JediKnight, level.Kind);
        Assert.Equal(2, level.Lights[0].Intensity);
        Assert.Equal(new ColorF(.2f, .4f, .6f), level.Lights[0].Color); // inactive JK source color remains editable
        Assert.Equal(3, level.Lights[0].RgbIntensity);
        Assert.Equal(9, level.Lights[0].LegacyRgbRange);
        Assert.Equal(new ColorF(.125f, .125f, .125f), level.Sectors[0].Surfaces[0].Corners[0].Intensity);
    }

    [Fact]
    public void EmptyThingParameterLineDoesNotConsumeNextRecord()
    {
        var source = Native.Replace("THS 1", "THS 2").Replace("frame=(0/0/0:0/0/0) health=100 frame=(1/2/3:0/0/0)",
            "\ncrate 8 9 10 0 0 0 0 0\nhealth=50");
        var level = JedImporter.ImportText(source, [BaseTemplate()]).Document.Level;
        Assert.Empty(level.Things[0].Values);
        Assert.Equal("50", level.Things[1].Values["health"]);
        Assert.Equal(new Vec3(8, 9, 10), level.Things[1].Position);
    }

    [Fact]
    public void MissingNativeInteriorCollisionBoxClearsOnlyItsShortcutFlag()
    {
        var result = JedImporter.ImportText(Native.Replace("ambient.wav 0.25 4", "ambient.wav 0.25 1004"), [BaseTemplate()]);
        Assert.Equal(4, result.Document.Level.Sectors[0].Flags);
        Assert.False(result.Document.Level.Sectors[0].HasCollideBox);
        Assert.Contains(result.Warnings, warning => warning.Contains("COLLIDEBOX"));
        Assert.Equal(4, JklParser.Parse(JklWriter.Build(result.Document)).Sectors[0].Flags);
    }

    [Fact]
    public void NativeAdjoinReferencesResolveAfterAllSectorsAreLoaded()
    {
        int start = Native.IndexOf("dflt.cmp ambient.wav", StringComparison.Ordinal);
        int end = Native.IndexOf("END\nSEC: THINGS", start, StringComparison.Ordinal);
        string sector = Native[start..end];
        string source = Native[..start].Replace("SECS 1", "SECS 2") +
            sector.Replace("3 -1 -1 0 1 1", "3 1 0 3 1 1") +
            sector.Replace("3 -1 -1 0 1 1", "3 0 0 3 1 1") + Native[end..];
        var level = JedImporter.ImportText(source, [BaseTemplate()]).Document.Level;
        Assert.Same(level.Sectors[1].Surfaces[0], level.Sectors[0].Surfaces[0].Adjoin);
        Assert.Same(level.Sectors[0].Surfaces[0], level.Sectors[1].Surfaces[0].Adjoin);
        Assert.Equal(3, level.Sectors[0].Surfaces[0].AdjoinFlags);
    }

    [Fact]
    public void NedCogGroupsAreReportedAsUnsupportedEditorMetadata()
    {
        var result = JedImporter.ImportText(Native + "\nCOGGROUPS\nGROUPS 1\nGroup one\nEND", [BaseTemplate()]);
        Assert.Contains(result.Warnings, w => w.Contains("COG-group"));
    }

    [Theory]
    [InlineData("SECS 1", "SECS -1")]
    [InlineData("VXS 3", "VXS 2147483647")]
    [InlineData("1 0 0\n0 1 0", "NaN 0 0\n0 1 0")]
    [InlineData("0 0 0 0.125 1 1 0 0.25", "9 0 0 0.125 1 1 0 0.25")]
    [InlineData("0 0 original.3do abcdef", "0 99 original.3do abcdef")]
    public void MalformedNativeDataIsRejectedAtomically(string before, string after) =>
        Assert.Throws<FormatException>(() => JedImporter.ImportText(Native.Replace(before, after), [BaseTemplate()]));

    [Fact]
    public void MissingEndAndUnknownSubstantiveSectionFailClearly()
    {
        Assert.Throws<FormatException>(() => JedImporter.ImportText(Native[..^3]));
        Assert.Throws<NotSupportedException>(() => JedImporter.ImportText(Native + "\nSEC: UNKNOWN\ndata\nEND"));
        Assert.Throws<NotSupportedException>(() => JedImporter.ImportText(Native.Replace("NODES 0", "NODES 1\nnode")));
    }

    [Fact]
    public void ProjectCompanionTemplatesAreLoadedWithoutChangingFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sed-jed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "sample.jed"), Native);
            const string templateText = "crate none model3d=original.3do frame=(0 / 1 / 2) frame=(3/4/5)";
            File.WriteAllText(Path.Combine(dir, "mots.tpl"), templateText);
            var result = JedImporter.ImportFile(Path.Combine(dir, "sample.jed"));
            Assert.Equal("original.3do", result.Document.Level.GetThingModel(result.Document.Level.Things[0]));
            Assert.Equal(3, result.Document.Level.Templates["crate"].SourceParameters.Count);
            Assert.Equal(templateText, File.ReadAllText(Path.Combine(dir, "mots.tpl")));
            var first = JedImporter.LoadTemplates(ProjectType.MysteriesOfTheSith, dir).Single(template => template.Name == "crate");
            first.Values["model3d"] = "edited.3do";
            var second = JedImporter.LoadTemplates(ProjectType.MysteriesOfTheSith, dir).Single(template => template.Name == "crate");
            Assert.Equal("original.3do", second.Values["model3d"]);
            Assert.Equal(3, second.SourceParameters.Count);
        }
        finally
        {
            File.Delete(Path.Combine(dir, "sample.jed"));
            File.Delete(Path.Combine(dir, "mots.tpl"));
            Directory.Delete(dir);
        }
    }
}

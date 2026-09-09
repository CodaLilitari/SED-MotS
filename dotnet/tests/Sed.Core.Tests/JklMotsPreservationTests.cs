using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public class JklMotsPreservationTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" cog=vader.cog")]
    public void LegacyEyeOffsetKeepsFollowingParameterAndOriginalSpelling(string following)
    {
        const string value = "(0.000000/0.000000/0.037000";
        string source = Source.Replace("model3d=original.3do", "model3d=original.3do eyeoffset=" + value + following)
            .Replace("health=75", "health=75 eyeoffset=" + value + following);
        var doc = JklParser.ParseDocument(source);
        Assert.Equal(value, doc.Level.Templates["crate"].Values["eyeoffset"]);
        Assert.Equal(value, doc.Level.Things[0].Values["eyeoffset"]);
        Assert.Equal(2, doc.PreservationWarnings.Count(w => w.Contains("legacy eyeoffset")));
        Assert.Equal(source, JklWriter.Build(doc));
        doc.Level.Things[0].Values["health"] = "76";
        doc.Level.Templates["crate"].Values["mass"] = "3";
        var back = JklParser.Parse(JklWriter.Build(doc));
        Assert.Equal(value, back.Templates["crate"].Values["eyeoffset"]);
        Assert.Equal(value, back.Things[0].Values["eyeoffset"]);
        if (following.Length > 0)
        {
            Assert.Equal("vader.cog", back.Templates["crate"].Values["cog"]);
            Assert.Equal("vader.cog", back.Things[0].Values["cog"]);
        }
    }

    [Theory]
    [InlineData("eyeoffset=(1/2")]
    [InlineData("eyeoffset=(1/2/3/4")]
    [InlineData("eyeoffset=(1,2,3")]
    [InlineData("eyeoffset=(1/2/NaN")]
    [InlineData("eyeoffset=(1/2/Infinity")]
    [InlineData("eyeoffset=(1/2/3oops")]
    [InlineData("eyeoffset=((1/2/3")]
    [InlineData("eyeoffset=(1 /2/3")]
    [InlineData("eyeoffset=(1/2/3 garbage")]
    [InlineData("offset=(1/2/3")]
    public void LegacyEyeOffsetExceptionRejectsOtherMalformedValues(string parameter) =>
        Assert.Throws<FormatException>(() => JklReader.ParameterTokens(parameter));

    [Fact]
    public void LegacyEyeOffsetAcceptsScientificNumbersWithoutChangingBalancedVectorHandling()
    {
        Assert.Equal(new[] { "eyeoffset=(-1e-3/+2.5E1/.3", "cog=vader.cog" },
            JklReader.ParameterTokens("eyeoffset=(-1e-3/+2.5E1/.3 cog=vader.cog"));
        Assert.Equal(new[] { "eyeoffset=(1/2/3 )", "cog=vader.cog" },
            JklReader.ParameterTokens("eyeoffset=(1/2/3 ) cog=vader.cog"));
    }

    private const string Source = """
        # Keep all original comments on a no-edit save.
        SECTION: HEADER
        VERSION 1
        END
        SECTION: MATERIALS
        World materials 1
        0: original.mat 0.25 0.75
        END
        SECTION: GEORESOURCE
        World colormaps 1
        0: dflt.cmp
        World vertices 4
        0: 0 0 0
        1: 1 0 0
        2: 0 1 0
        3: 0.000001 0 0
        World texture vertices 1
        0: 0 0
        World adjoins 0
        World surfaces 1
        0: 0 0 0 4 3 3 -1 0.5 3 0,0 1,0 2,0 0.125 0.25 0.5 0.75 0.2 0.3 0.4 0.5 0.6 0.7 0.8 0.9
        0: 0 0 1
        END
        SECTION: SECTORS
        World sectors 1
        SECTOR 0
        FLAGS 0
        THRUST 1 2 3
        COLLIDEBOX -1 -2 -3 4 5 6
        BOUNDBOX -10 -20 -30 40 50 60
        CENTER 2 3 4
        RADIUS 99
        VERTICES 4
        0: 0
        1: 1
        2: 2
        3: 3
        SURFACES 0 1
        END
        SECTION: TEMPLATES
        World templates 1
        crate none model3d=original.3do
        END
        SECTION: THINGS
        World things 8
        0: crate ActualName 0 0 0 0 0 0 0 0 health=75
        END
        SECTION: ARCHLIGHTING
        Num ArchObjects 1
        archobject 0
        nummeshes: 1
        mesh 0
        numvertices: 1
        0: 0.125 0.25 0.5 0.75
        END
        SECTION: LAYERS
        Editor layers 2
        #Ground floor
        0:
        0:
        #Objects and sectors
        1: 0
        1: 0
        END
        SECTION: CUSTOM
        opaque payload KEEP me
        END
        """;

    [Fact]
    public void NoEditSavePreservesAllSourceLines()
    {
        var doc = JklParser.ParseDocument(Source);
        Assert.Equal(Source, JklWriter.Build(doc));
        Assert.Equal(ProjectType.MysteriesOfTheSith, doc.Level.Kind);
        Assert.Equal(0.125f, doc.Level.Sectors[0].Surfaces[0].Corners[0].MonoIntensity);
        Assert.Equal(new ColorF(.25f, .5f, .75f), doc.Level.Sectors[0].Surfaces[0].Corners[0].Intensity);
        Assert.Equal(4, doc.Level.Sectors[0].Vertices.Count); // spare close vertex is retained
    }

    [Fact]
    public void EditedGeometryKeepsIndependentMonoRgbAndNearbyVertices()
    {
        var doc = JklParser.ParseDocument(Source);
        doc.Level.Sectors[0].Surfaces[0].Corners[0].Intensity = new(.91f, .81f, .71f);
        var back = JklParser.ParseDocument(JklWriter.Build(doc));
        var corner = back.Level.Sectors[0].Surfaces[0].Corners[0];
        Assert.Equal(.125f, corner.MonoIntensity);
        Assert.Equal(new ColorF(.91f, .81f, .71f), corner.Intensity);
        Assert.Equal(4, back.Level.Sectors[0].Vertices.Count);
    }

    [Fact]
    public void ThingsKeepCapacityBindingAndUnassignedSector()
    {
        var doc = JklParser.ParseDocument(Source);
        doc.Level.Things[0].Sector = null;
        doc.Level.Things[0].Position = new(1.123456789, 2, 3);
        var back = JklParser.ParseDocument(JklWriter.Build(doc));
        Assert.Equal(8, back.Level.ThingCapacity);
        Assert.Equal(0, back.Level.Things[0].ArchLightingIndex);
        Assert.Null(back.Level.Things[0].Sector);
        Assert.Equal(1.123456789, back.Level.Things[0].Position.X);
        Assert.Equal(new ArchitecturalLightVertex(.125, .25, .5, .75),
            back.Level.ArchitecturalLighting[0].Meshes[0].Vertices[0]);
    }

    [Fact]
    public void NewMotSThingWritesUnboundColumn()
    {
        var doc = JklParser.ParseDocument(Source);
        var thing = doc.Level.NewThing();
        thing.Template = "crate";
        thing.Name = "new";
        var back = JklParser.Parse(JklWriter.Build(doc));
        Assert.Equal(-1, back.Things[1].ArchLightingIndex);
        Assert.Null(back.Things[1].Sector);
    }

    [Fact]
    public void ChangedBoundModelIsRefusedWithoutModifyingOriginalBinding()
    {
        var doc = JklParser.ParseDocument(Source);
        doc.Level.Templates["crate"].Values["model3d"] = "other.3do";
        Assert.Throws<InvalidOperationException>(() => JklWriter.Build(doc));
        Assert.Equal(0, doc.Level.Things[0].ArchLightingIndex);
    }

    [Fact]
    public void EditedSectorKeepsThrustCollisionAndAuthoredBounds()
    {
        var doc = JklParser.ParseDocument(Source);
        doc.Level.Sectors[0].Thrust = new(9, 8, 7);
        var sector = JklParser.Parse(JklWriter.Build(doc)).Sectors[0];
        Assert.Equal(new Vec3(9, 8, 7), sector.Thrust);
        Assert.True(sector.HasCollideBox);
        Assert.Equal(new Vec3(-1, -2, -3), sector.CollideBox.Min);
        Assert.Equal(new Vec3(-10, -20, -30), sector.BoundBox!.Value.Min);
        Assert.Equal(new Vec3(2, 3, 4), sector.Center);
        Assert.Equal(99, sector.Radius);
    }

    [Fact]
    public void LayerNamesAndMembershipSurviveActualRewrite()
    {
        var doc = JklParser.ParseDocument(Source);
        doc.Level.Layers[1] = "Renamed layer";
        var level = JklParser.Parse(JklWriter.Build(doc));
        Assert.Equal(new[] { "Ground floor", "Renamed layer" }, level.Layers);
        Assert.Equal(1, level.Sectors[0].Layer);
        Assert.Equal(1, level.Things[0].Layer);
    }

    [Fact]
    public void MaterialNameEditAppendsWithoutLosingOriginalRows()
    {
        var doc = JklParser.ParseDocument(Source);
        doc.Level.Sectors[0].Surfaces[0].Material = "new.mat";
        var output = JklWriter.Build(doc);
        Assert.Contains("0: original.mat 0.25 0.75", output);
        var back = JklParser.ParseDocument(output);
        Assert.Equal(new[] { "original.mat", "new.mat" }, back.Materials);
        Assert.Equal(1, back.Level.Sectors[0].Surfaces[0].MaterialIndex);
        Assert.Equal("new.mat", back.Level.Sectors[0].Surfaces[0].Material);
    }

    [Theory]
    [InlineData("0.125 0.25 0.5 0.75 0.2 0.3 0.4 0.5 0.6 0.7 0.8 0.9", "0 1 2 3")]
    [InlineData("Num ArchObjects 1", "Num ArchObjects -1")]
    [InlineData("Num ArchObjects 1", "Num ArchObjects 2147483647")]
    [InlineData("0: 0.125 0.25 0.5 0.75", "0: NaN 0.25 0.5 0.75")]
    [InlineData("0: 0.125 0.25 0.5 0.75", "1: 0.125 0.25 0.5 0.75")]
    [InlineData("World vertices 4", "World vertices -1")]
    [InlineData("0 0 health=75", "0 999 health=75")]
    public void MalformedInputFailsClearly(string original, string replacement) =>
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(Source.Replace(original, replacement)));

    [Fact]
    public void MissingArchEndIsRejected()
    {
        string input = Source.Replace("0: 0.125 0.25 0.5 0.75\nEND", "0: 0.125 0.25 0.5 0.75");
        Assert.Throws<FormatException>(() => JklParser.ParseDocument(input));
    }

    [Fact]
    public void UnknownFieldPreservedUntouchedButBlocksItsSectionRewrite()
    {
        var source = Source.Replace("VERSION 1", "VERSION 1\nUNKNOWN HEADER FIELD 7");
        var doc = JklParser.ParseDocument(source);
        Assert.Equal(source, JklWriter.Build(doc));
        Assert.NotEmpty(doc.PreservationWarnings);
        doc.Level.Header.Gravity = 9;
        Assert.Throws<InvalidOperationException>(() => JklWriter.Build(doc));
    }

    [Fact]
    public void ArchitecturalObjectCloneOwnsItsNestedLists()
    {
        var original = JklParser.ParseDocument(Source).Level.ArchitecturalLighting[0];
        var copy = original.Clone();
        copy.Meshes[0].Vertices.Clear();
        Assert.Single(original.Meshes[0].Vertices);
    }

    [Fact]
    public void DistinctCoincidentSourceVerticesRemainDistinctAfterRewrite()
    {
        var doc = JklParser.ParseDocument(Source.Replace("3: 0.000001 0 0", "3: 0 0 0"));
        doc.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .8f;
        var back = JklParser.Parse(JklWriter.Build(doc));
        Assert.Equal(4, back.Sectors[0].Vertices.Count);
        Assert.Equal(back.Sectors[0].Vertices[0].Position, back.Sectors[0].Vertices[3].Position);
        Assert.NotSame(back.Sectors[0].Vertices[0], back.Sectors[0].Vertices[3]);
    }

    [Fact]
    public void MovingThingRetainsEveryOrderedFrameAndUnrelatedParameterEdit()
    {
        var doc = JklParser.ParseDocument(Source.Replace("health=75", "frame=(0/0/0:0/0/0) health=75 frame=(1 / 2 / 3:0/0/0)"));
        doc.Level.Things[0].Position = new(8, 9, 10);
        doc.Level.Things[0].Values["health"] = "90";
        var back = JklParser.Parse(JklWriter.Build(doc));
        Assert.Equal(new[] { "frame", "health", "frame" }, back.Things[0].SourceParameters.Select(p => p.Key));
        Assert.Equal("(0/0/0:0/0/0)", back.Things[0].SourceParameters[0].Value);
        Assert.Equal("(1 / 2 / 3:0/0/0)", back.Things[0].SourceParameters[2].Value);
        Assert.Equal("90", back.Things[0].Values["health"]);
    }

    [Fact]
    public void AmbiguousSingleValueFrameEditIsRefused()
    {
        var doc = JklParser.ParseDocument(Source.Replace("health=75", "frame=(0/0/0) frame=(1/1/1)"));
        doc.Level.Things[0].Values["frame"] = "(3/3/3)";
        Assert.Throws<InvalidOperationException>(() => JklWriter.Build(doc));
    }

    [Fact]
    public void EngineAcceptedFinalFrameWithoutClosingParenthesisIsPreservedAndReported()
    {
        const string frame = "frame=(-2.0/-3.8/-0.1:0.0/180/180";
        string source = Source.Replace("health=75", "health=75 " + frame);
        var document = JklParser.ParseDocument(source);
        Assert.Equal(source, JklWriter.Build(document));
        Assert.Contains(document.PreservationWarnings, warning => warning.Contains("no closing parenthesis"));
        document.Level.Things[0].Position = new(1, 2, 3);
        var regenerated = JklWriter.Build(document);
        Assert.Contains(frame, regenerated);
        Assert.Equal(frame[6..], JklParser.Parse(regenerated).Things[0].Values["frame"]);
    }

    [Theory]
    [InlineData("frame=(1/2/3:4/5")]
    [InlineData("frame=(1/2/3:4/5/6 health=75")]
    [InlineData("frame=(1/2/3:4/5/Infinity")]
    [InlineData("orient=(1/2/3:4/5/6")]
    [InlineData("frame=((1/2/3:4/5/6")]
    public void OtherUnclosedParameterFormsRemainRejected(string parameter) =>
        Assert.Throws<FormatException>(() => JklReader.ParameterTokens(parameter));

    [Fact]
    public void EditingTemplateKeepsRepeatedAndSpacedParameters()
    {
        var doc = JklParser.ParseDocument(Source.Replace("crate none model3d=original.3do",
            "crate none model3d=original.3do frame=(0 / 1 / 2) mass=3 frame=(3/4/5)"));
        doc.Level.Templates["crate"].Values["mass"] = "9";
        var template = JklParser.Parse(JklWriter.Build(doc)).Templates["crate"];
        Assert.Equal(new[] { "(0 / 1 / 2)", "(3/4/5)" },
            template.SourceParameters.Where(p => p.Key == "frame").Select(p => p.Value));
        Assert.Equal("9", template.Values["mass"]);
    }
}

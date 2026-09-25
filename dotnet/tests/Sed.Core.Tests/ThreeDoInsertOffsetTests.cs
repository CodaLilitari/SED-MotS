using System.Globalization;
using Sed.Core.Math;
using Sed.Formats.ThreeDo;

namespace Sed.Core.Tests;

public sealed class ThreeDoInsertOffsetTests
{
    [Theory]
    [InlineData(2.1)]
    [InlineData(2.2)]
    public void ImportedNonzeroInsertionOffsetSurvivesSaveAndReopen(double version)
    {
        var model = ThreeDoParser.Parse(Triangle);
        var offset = new Vec3(1.25, -2.5, 0.75);
        var pivot = new Vec3(0.125, -0.25, 0.5);
        Assert.Equal(offset, model.InsertOffset);
        Assert.Equal(pivot, model.Nodes[0].Pivot);

        var reloaded = ThreeDoParser.Parse(ThreeDoWriter.Build(model, version));

        Assert.Equal(offset, reloaded.InsertOffset);
        Assert.Equal(pivot, reloaded.Nodes[0].Pivot);
        Assert.Equal(model.Meshes[0].Vertices, reloaded.Meshes[0].Vertices);
        Assert.Equal(offset, model.InsertOffset);
        Assert.Equal(pivot, model.Nodes[0].Pivot);
    }

    [Fact]
    public void InsertionOffsetRetainsDoublePrecisionAndInvariantFormatting()
    {
        var model = ThreeDoParser.Parse(Triangle);
        model.InsertOffset = new Vec3(0.00000001234567891234567, -2.1234567891234567, 0.75);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var reloaded = ThreeDoParser.Parse(ThreeDoWriter.Build(model));
            Assert.Equal(model.InsertOffset, reloaded.InsertOffset);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void ZeroInsertionOffsetKeepsExistingOutput()
    {
        var model = ThreeDoParser.Parse(Triangle);
        model.InsertOffset = Vec3.Zero;
        var written = ThreeDoWriter.Build(model);
        Assert.Contains("INSERT OFFSET 0 0 0", written);
        Assert.Equal(Vec3.Zero, ThreeDoParser.Parse(written).InsertOffset);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0)]
    [InlineData(0, double.PositiveInfinity, 0)]
    [InlineData(0, 0, double.NegativeInfinity)]
    public void NonfiniteInsertionOffsetIsRefused(double x, double y, double z)
    {
        var model = ThreeDoParser.Parse(Triangle);
        model.InsertOffset = new Vec3(x, y, z);
        var error = Assert.Throws<FormatException>(() => ThreeDoWriter.Build(model));
        Assert.Contains("insertion offset", error.Message);
    }

    // Original asset-free triangle with distinct insertion and hierarchy pivots.
    private const string Triangle = """
        SECTION: HEADER
        3DO 2.1
        SECTION: MODELRESOURCE
        MATERIALS 0
        SECTION: GEOMETRYDEF
        RADIUS 2
        INSERT OFFSET 1.25 -2.5 0.75
        GEOSETS 1
        GEOSET 0
        MESHES 1
        MESH 0
        NAME synthetic
        RADIUS 2
        GEOMETRYMODE 4
        LIGHTINGMODE 3
        TEXTUREMODE 3
        VERTICES 3
        0: 0 0 0 0.25
        1: 1 0 0 0.5
        2: 0 1 0 0.75
        TEXTURE VERTICES 0
        VERTEX NORMALS
        0: 0 0 1
        1: 0 0 1
        2: 0 0 1
        FACES 1
        0: -1 0 3 3 3 0 3 0,-1 1,-1 2,-1
        FACE NORMALS
        0: 0 0 1
        SECTION: HIERARCHYDEF
        HIERARCHY NODES 1
        0: 0 1 0 -1 -1 -1 0 0 0 0 0 0 0 0.125 -0.25 0.5 root
        END
        """;
}

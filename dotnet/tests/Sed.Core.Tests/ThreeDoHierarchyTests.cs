using System.Globalization;
using Sed.Core.Math;
using Sed.Formats.ThreeDo;
using Xunit;

namespace Sed.Core.Tests;

/// <summary>Original fixture, following JK/MotS rdModel3's 18-field hierarchy row contract.</summary>
public sealed class ThreeDoHierarchyTests
{
    [Theory]
    [InlineData(2.1)]
    [InlineData(2.2)]
    public void AuthoredNodeMetadataSurvivesLoadEditAndSave(double version)
    {
        var model = ThreeDoParser.Parse(Fixture(version));
        AssertOriginalNodes(model);

        // A mesh rename must not replace an independent authored node name.
        model.Meshes[0].Name = "RenamedMesh";
        model.Nodes[3].Name = "Edited_Hand";
        model.Nodes[3].Pivot = new Vec3(0.0000000123456789, -0.25, 0.75);
        string written = ThreeDoWriter.Build(model, version);
        var reloaded = ThreeDoParser.Parse(written);

        Assert.Equal(version, reloaded.Version);
        for (int i = 0; i < model.Nodes.Count; i++) AssertNodeEqual(model.Nodes[i], reloaded.Nodes[i]);
        Assert.Equal("Rig_Root", reloaded.Nodes[0].Name);
        Assert.Equal("Left_Shoulder", reloaded.Nodes[1].Name);
        Assert.Equal("Edited_Hand", reloaded.Nodes[3].Name);
        Assert.Equal(new Vec3(0.0000000123456789, -0.25, 0.75), reloaded.Nodes[3].Pivot);
        Assert.Equal("0x80000000", HierarchyTokens(written)[0][1]);
        Assert.Equal("0xffffffff", HierarchyTokens(written)[3][2]);
    }

    [Fact]
    public void ParentEditsRegenerateConsistentChildAndSiblingFields()
    {
        var model = ThreeDoParser.Parse(Fixture(2.2));
        model.Nodes[3].Parent = 0;
        var rows = HierarchyTokens(ThreeDoWriter.Build(model, 2.2));

        // Tokens include the row index; child/sibling/count occupy columns 5, 6 and 7.
        Assert.Equal(new[] { "1", "-1", "3" }, rows[0][5..8]);
        Assert.Equal(new[] { "-1", "2", "0" }, rows[1][5..8]);
        Assert.Equal(new[] { "-1", "3", "0" }, rows[2][5..8]);
        Assert.Equal(new[] { "-1", "-1", "0" }, rows[3][5..8]);
    }

    [Fact]
    public void NewNodesUseHistoricalTypeAndNameDefaults()
    {
        var model = new ThreeDoModel();
        model.Meshes.Add(new Mesh3do { Name = "NewMesh" });
        model.Nodes.Add(new HierarchyNode { Mesh = 0 });
        model.Nodes.Add(new HierarchyNode());

        var reloaded = ThreeDoParser.Parse(ThreeDoWriter.Build(model));
        Assert.Equal("NewMesh", reloaded.Nodes[0].Name);
        Assert.Equal("$$DUMMY", reloaded.Nodes[1].Name);
        Assert.All(reloaded.Nodes, node =>
        {
            Assert.Equal(0u, node.Flags);
            Assert.Equal(1u, node.Type);
            Assert.Equal(Vec3.Zero, node.Pivot);
        });
    }

    [Theory]
    [InlineData("0: 0 1 0 -1 -1 -1 0 0 0 0 0 0 0")]
    [InlineData("0: 0 1 0 -1 -1 -1 0 0 0 0 0 0 0 1 2 3")]
    [InlineData("0: 0x100000000 1 0 -1 -1 -1 0 0 0 0 0 0 0 1 2 3 root")]
    [InlineData("0: 0 0x100000000 0 -1 -1 -1 0 0 0 0 0 0 0 1 2 3 root")]
    [InlineData("0: 0 1 0 -1 -1 -1 0 0 0 0 0 0 0 NaN 2 3 root")]
    public void IncompleteOrUnrepresentableNodeDataIsRefused(string row)
        => Assert.Throws<FormatException>(() => ThreeDoParser.Parse(
            "SECTION: HIERARCHYDEF\nHIERARCHY NODES 1\n" + row + "\nEND\n"));

    [Theory]
    [InlineData("two words")]
    [InlineData("root#comment")]
    [InlineData("root\nEND")]
    public void NodeNamesThatWouldChangeTheOutputStructureAreRefused(string name)
    {
        var model = new ThreeDoModel();
        model.Nodes.Add(new HierarchyNode { Name = name });
        Assert.Throws<FormatException>(() => ThreeDoWriter.Build(model));
    }

    private static void AssertOriginalNodes(ThreeDoModel model)
    {
        Assert.Equal(4, model.Nodes.Count);
        Assert.Equal(0x80000000u, model.Nodes[0].Flags);
        Assert.Equal(0x20u, model.Nodes[0].Type);
        Assert.Equal("Rig_Root", model.Nodes[0].Name);
        Assert.Equal(new Vec3(1.25, -2.5, 3.75), model.Nodes[0].Offset);
        Assert.Equal(new Vec3(0.125, -0.25, 0.5), model.Nodes[0].Pivot);
        Assert.Equal(10.0, model.Nodes[0].Pitch);
        Assert.Equal(-20.0, model.Nodes[0].Yaw);
        Assert.Equal(30.0, model.Nodes[0].Roll);
        Assert.Equal("Left_Shoulder", model.Nodes[1].Name);
        Assert.Equal(0x8u, model.Nodes[1].Flags);
        Assert.Equal(0x4u, model.Nodes[1].Type);
        Assert.Equal(new Vec3(-0.75, 0.875, -1.0), model.Nodes[1].Pivot);
        Assert.Equal("Right_Shoulder", model.Nodes[2].Name);
        Assert.Equal(0x10000u, model.Nodes[2].Type);
        Assert.Equal("Hand_End", model.Nodes[3].Name);
        Assert.Equal(uint.MaxValue, model.Nodes[3].Type);
        Assert.Equal(1, model.Nodes[3].Parent);
    }

    private static void AssertNodeEqual(HierarchyNode expected, HierarchyNode actual)
    {
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Mesh, actual.Mesh);
        Assert.Equal(expected.Parent, actual.Parent);
        Assert.Equal(expected.Offset, actual.Offset);
        Assert.Equal(expected.Pitch, actual.Pitch);
        Assert.Equal(expected.Yaw, actual.Yaw);
        Assert.Equal(expected.Roll, actual.Roll);
        Assert.Equal(expected.Pivot, actual.Pivot);
    }

    private static string[][] HierarchyTokens(string text)
    {
        int section = text.IndexOf("SECTION: HIERARCHYDEF", StringComparison.Ordinal);
        return text[section..].Split('\n').Select(line => line.Trim())
            .Where(line => line.Length > 0 && char.IsAsciiDigit(line[0]))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray();
    }

    private static string Fixture(double version) => $$"""
        # Original synthetic articulated triangle; no game assets.
        SECTION: HEADER
        3DO {{version.ToString("0.0", CultureInfo.InvariantCulture)}}
        SECTION: MODELRESOURCE
        MATERIALS 1
        0: synthetic.mat
        SECTION: GEOMETRYDEF
        RADIUS 4
        INSERT OFFSET 0 0 0
        GEOSETS 1
        GEOSET 0
        MESHES 1
        MESH 0
        NAME MeshOnly
        GEOMETRYMODE 4
        LIGHTINGMODE 3
        TEXTUREMODE 3
        VERTICES 3
        0: 0 0 0 0.25
        1: 1 0 0 0.5
        2: 0 1 0 0.75
        TEXTURE VERTICES 3
        0: 0 0
        1: 1 0
        2: 0 1
        FACES 1
        0: 0 0 4 3 3 0 3 0,0 1,1 2,2
        SECTION: HIERARCHYDEF
        HIERARCHY NODES 4
        0: 0x80000000 0x20 -1 -1 1 -1 2 1.25 -2.5 3.75 10 -20 30 0.125 -0.25 0.5 Rig_Root
        1: 0x8 0x4 0 0 3 2 1 -1 2 -3 45 90 180 -0.75 0.875 -1 Left_Shoulder
        2: 0 0x10000 0 0 -1 -1 0 1 2 3 -45 -90 -180 0.1 0.2 0.3 Right_Shoulder
        3: 0 0xffffffff 0 1 -1 -1 0 0.5 0.25 0.125 0 0 0 0.0000000123456789 0 0 Hand_End
        END
        """;
}

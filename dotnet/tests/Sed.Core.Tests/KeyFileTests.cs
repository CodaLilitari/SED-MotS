using Sed.Formats.Keyframe;
using Xunit;

namespace Sed.Core.Tests;

/// <summary>KEY cutscene animation files (PJKEY_IO.INC / U_PJKEY.PAS).</summary>
public class KeyFileTests
{
    private const string Key = """
        SECTION: HEADER
        FLAGS 0
        TYPE 0
        FRAMES 30
        FPS 15
        JOINTS 0

        SECTION: KEYFRAME
        NODES 1
        NODE 0
        MESH NAME table
        ENTRIES 2
        rest 0 0x1 0.0 0.0 0.0 0.0 0.0 0.0
        0.0 0.0 0.0 0.0 0.0 0.0
        rest 30 0x1 0.0 0.0 0.0 0.0 0.0 0.0
        0.0 0.0 0.0 0.0 0.0 0.0
        END
        """;

    [Fact]
    public void ParsesHeaderAndNodes()
    {
        var key = KeyFile.Parse(Key);

        Assert.Equal(30, key.FrameCount);
        Assert.Equal(15, key.Fps);
        Assert.Equal(0, key.Flags);

        var node = Assert.Single(key.Nodes);
        Assert.Equal("TABLE", node.MeshName); // the reader upper-cases lines
        Assert.Equal(2, node.Entries.Count);
        Assert.Equal(0, node.Entries[0].Frame);
        Assert.Equal(30, node.Entries[1].Frame);
    }

    [Fact]
    public void InterpolatesFromTheLastEntryAtOrBeforeTheFrame()
    {
        var key = KeyFile.Parse(Key);
        var node = key.Nodes[0];

        node.Entries[0].CX = 10;
        node.Entries[0].DX = 2;
        node.Entries[1].CX = 70;
        node.Entries[1].DX = 1;

        // Frame 5 is between the entries: uses entry 0 → 10 + 2·5 = 20.
        Assert.True(node.GetFrame(5, out var x, out _, out _, out _, out _, out _));
        Assert.Equal(20, x);

        // Frame 0: the entry itself.
        node.GetFrame(0, out x, out _, out _, out _, out _, out _);
        Assert.Equal(10, x);

        // Frame 40 past the last entry: extrapolates 70 + 1·(40−30) = 80.
        node.GetFrame(40, out x, out _, out _, out _, out _, out _);
        Assert.Equal(80, x);
    }

    [Fact]
    public void PoseAndDeltaLinesAreBothParsed()
    {
        const string twoLine = """
            SECTION: HEADER
            FRAMES 10
            FPS 15

            SECTION: KEYFRAME
            NODES 1
            NODE 0
            MESH NAME box
            ENTRIES 1
            rest 0 0x3 1.0 2.0 3.0 4.0 5.0 6.0
            0.1 0.2 0.3 0.4 0.5 0.6
            END
            """;

        var key = KeyFile.Parse(twoLine);
        var entry = Assert.Single(Assert.Single(key.Nodes).Entries);

        Assert.Equal(0x3, entry.Flags);
        Assert.Equal(1.0, entry.CX);
        Assert.Equal(2.0, entry.CY);
        Assert.Equal(3.0, entry.CZ);
        Assert.Equal(0.1, entry.DX);
        Assert.Equal(0.2, entry.DY);
        Assert.Equal(0.3, entry.DZ);
        Assert.Equal(0.6, entry.DRol);
    }

    [Fact]
    public void EmptyEntriesReportNoFrame()
    {
        var key = KeyFile.Parse(Key);
        key.Nodes[0].Entries.Clear();
        AssertNoPose(key.Nodes[0], 5);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(9)]
    public void AcceptedDelayedFirstEntryHasNoPoseBeforeItsStart(int frame)
    {
        var node = Assert.Single(KeyFile.Parse(DelayedKey).Nodes);
        AssertNoPose(node, frame);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(11, 1)]
    [InlineData(20, 10)]
    public void DelayedFirstEntryStartsAtItsAuthoredFrameAndKeepsForwardSampling(int frame, int delta)
    {
        var node = Assert.Single(KeyFile.Parse(DelayedKey).Nodes);
        Assert.True(node.GetFrame(frame, out var x, out var y, out var z,
            out var pitch, out var yaw, out var roll));
        Assert.Equal(new double[] { 1 + delta, 2 + 2 * delta, 3 + 3 * delta,
            4 + 4 * delta, 5 + 5 * delta, 6 + 6 * delta },
            new[] { x, y, z, pitch, yaw, roll });
    }

    [Fact]
    public void NegativeRequestBeforeAnOrdinaryFrameZeroEntryHasNoPose()
        => AssertNoPose(KeyFile.Parse(Key).Nodes[0], -1);

    [Fact]
    public void EntryBoundarySelectsTheNewEntry()
    {
        var node = KeyFile.Parse(Key).Nodes[0];
        node.Entries[0].CX = 10;
        node.Entries[0].DX = 2;
        node.Entries[1].CX = 123;
        node.Entries[1].DX = 1;

        Assert.True(node.GetFrame(29, out var before, out _, out _, out _, out _, out _));
        Assert.Equal(68, before);
        Assert.True(node.GetFrame(30, out var boundary, out _, out _, out _, out _, out _));
        Assert.Equal(123, boundary);
        Assert.True(node.GetFrame(31, out var after, out _, out _, out _, out _, out _));
        Assert.Equal(124, after);
    }

    private static void AssertNoPose(KeyNode node, int frame)
    {
        Assert.False(node.GetFrame(frame, out var x, out var y, out var z,
            out var pitch, out var yaw, out var roll));
        Assert.Equal(new double[6], new[] { x, y, z, pitch, yaw, roll });
    }

    // Original input reproducing the accepted-file/frame-zero preview failure.
    private const string DelayedKey = """
        SECTION: HEADER
        FRAMES 21
        FPS 15
        SECTION: KEYFRAME
        NODES 1
        NODE 0
        MESH NAME synthetic
        ENTRIES 1
        0: 10 0x3 1 2 3 4 5 6
        1 2 3 4 5 6
        END
        """;
}

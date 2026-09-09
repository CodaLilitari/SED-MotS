using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Validation;

namespace Sed.Core.Tests;

public sealed class CleaveSectorSafetyTests
{
    private static (Level level, Sector sector) Box()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var sector = SectorFactory.CreateBox(level, Vec3.Zero, 1, "test.mat", 0);
        level.Sectors.Add(sector);
        return (level, sector);
    }

    [Fact]
    public void UndoAndRepeatedRedoRestoreVertexOwnershipAndThingMembership()
    {
        var (level, sector) = Box();
        var vertices = sector.Vertices.ToArray();
        var corners = sector.Surfaces.Select(s => s.Corners.Select(c => c.Vertex).ToArray()).ToArray();
        var front = level.NewThing(); front.Sector = sector; front.Position = new Vec3(.5, 0, 0);
        var back = level.NewThing(); back.Sector = sector; back.Position = new Vec3(-.5, 0, 0);
        var onPlane = level.NewThing(); onPlane.Sector = sector; onPlane.Position = Vec3.Zero;
        var command = new CleaveSectorCommand(level, sector, new Vec3(1, 0, 0), Vec3.Zero);
        command.Apply();
        Assert.True(command.Succeeded, command.FailureReason);
        var newSector = command.NewSector!;
        Assert.True(newSector.Id > 0);
        for (int pass = 0; pass < 3; pass++)
        {
            Assert.Same(newSector, back.Sector);
            Assert.Same(sector, front.Sector);
            Assert.Same(sector, onPlane.Sector);
            Assert.All(level.Sectors, s => Assert.All(s.Vertices, v => Assert.Same(s, v.Sector)));
            Assert.DoesNotContain(ConsistencyChecker.Check(level), i => i.Severity == IssueSeverity.Error);
            command.Revert();
            Assert.Single(level.Sectors);
            Assert.All(vertices, v => Assert.Same(sector, v.Sector));
            Assert.Equal(vertices, sector.Vertices);
            for (int si = 0; si < sector.Surfaces.Count; si++)
                Assert.Equal(corners[si], sector.Surfaces[si].Corners.Select(c => c.Vertex));
            Assert.All(level.Things, t => Assert.Same(sector, t.Sector));
            command.Apply();
            Assert.Same(newSector, command.NewSector);
        }
    }

    [Fact]
    public void MotsMonoLightIsCopiedAndInterpolatedIndependentlyOfRgb()
    {
        var (level, sector) = Box();
        foreach (var surface in sector.Surfaces)
            foreach (var corner in surface.Corners)
            {
                corner.Intensity = new ColorF(.1f, .2f, .3f);
                corner.MonoIntensity = corner.Vertex.Position.X < 0 ? .25f : .75f;
            }
        var command = new CleaveSectorCommand(level, sector, new Vec3(1, 0, 0), Vec3.Zero);
        command.Apply();
        Assert.True(command.Succeeded, command.FailureReason);
        foreach (var surface in level.Sectors.SelectMany(s => s.Surfaces).Where(s => s.Adjoin is null))
            foreach (var corner in surface.Corners)
            {
                float expected = corner.Vertex.Position.X < -1e-6 ? .25f : corner.Vertex.Position.X > 1e-6 ? .75f : .5f;
                Assert.Equal(expected, corner.MonoIntensity);
                Assert.Equal(.1f, corner.Intensity.R);
            }
        command.Revert();
        Assert.All(sector.Surfaces.SelectMany(s => s.Corners), c =>
            Assert.Equal(c.Vertex.Position.X < 0 ? .25f : .75f, c.MonoIntensity));
    }

    [Fact]
    public void CrossingAnExistingPortalIsRefusedBeforeTopologyChanges()
    {
        var (level, sector) = Box();
        var neighbor = SectorFactory.CreateBox(level, new Vec3(0, 0, -2), 1, "other.mat", 0);
        level.Sectors.Add(neighbor);
        var portal = sector.Surfaces[0];
        var partner = neighbor.Surfaces[1];
        portal.Adjoin = partner; partner.Adjoin = portal;
        var vertices = sector.Vertices.ToArray();
        var portalVertices = portal.Corners.Select(c => c.Vertex).ToArray();
        var command = new CleaveSectorCommand(level, sector, new Vec3(1, 0, 0), Vec3.Zero);
        command.Apply();
        Assert.False(command.Succeeded);
        Assert.Contains("portal", command.FailureReason);
        Assert.Null(command.NewSector);
        Assert.Equal(2, level.Sectors.Count);
        Assert.Equal(vertices, sector.Vertices);
        Assert.Equal(portalVertices, portal.Corners.Select(c => c.Vertex));
        Assert.Same(partner, portal.Adjoin);
        Assert.Same(portal, partner.Adjoin);
        command.Revert();
        Assert.Equal(vertices, sector.Vertices);
    }

    [Fact]
    public void PortalWhollyBehindPlaneMovesAndUndoKeepsItsPartner()
    {
        var (level, sector) = Box();
        var neighbor = SectorFactory.CreateBox(level, new Vec3(-2, 0, 0), 1, "other.mat", 0);
        level.Sectors.Add(neighbor);
        var portal = sector.Surfaces[4]; var partner = neighbor.Surfaces[5];
        portal.Adjoin = partner; partner.Adjoin = portal;
        var command = new CleaveSectorCommand(level, sector, new Vec3(1, 0, 0), Vec3.Zero);
        command.Apply();
        Assert.True(command.Succeeded, command.FailureReason);
        Assert.Same(command.NewSector, portal.Sector);
        Assert.Same(partner, portal.Adjoin);
        Assert.Same(portal, partner.Adjoin);
        command.Revert();
        Assert.Same(sector, portal.Sector);
        Assert.All(portal.Corners, c => Assert.Same(sector, c.Vertex.Sector));
        command.Apply();
        Assert.Same(command.NewSector, portal.Sector);
    }

    [Fact]
    public void OpenCrossSectionRollsBackVerticesAndSurfaceSplits()
    {
        var (level, sector) = Box();
        sector.Surfaces.RemoveAt(0); // Missing z wall leaves an open cut outline.
        var vertices = sector.Vertices.ToArray();
        var surfaces = sector.Surfaces.ToArray();
        var corners = surfaces.Select(s => s.Corners.Select(c => c.Vertex).ToArray()).ToArray();
        var command = new CleaveSectorCommand(level, sector, new Vec3(1, 0, 0), Vec3.Zero);
        command.Apply();
        Assert.False(command.Succeeded);
        Assert.Contains("cross-section", command.FailureReason);
        Assert.Single(level.Sectors);
        Assert.Equal(vertices, sector.Vertices);
        Assert.Equal(surfaces, sector.Surfaces);
        for (int i = 0; i < surfaces.Length; i++) Assert.Equal(corners[i], surfaces[i].Corners.Select(c => c.Vertex));
        Assert.All(vertices, v => Assert.Same(sector, v.Sector));
    }

    [Fact]
    public void InteriorCollisionShortcutIsInvalidatedAndRestoredByUndo()
    {
        var (level, sector) = Box();
        sector.Flags = 0x1002;
        sector.HasCollideBox = true;
        sector.CollideBox = new(new Vec3(-.9, -.9, -.9), new Vec3(.9, .9, .9));
        var box = sector.CollideBox;
        var command = new CleaveSectorCommand(level, sector, new Vec3(1, 1, 0), Vec3.Zero);
        command.Apply();
        Assert.True(command.Succeeded, command.FailureReason);
        Assert.All(level.Sectors, s => { Assert.False(s.HasCollideBox); Assert.Equal(0L, s.Flags & 0x1000); });
        command.Revert();
        Assert.True(sector.HasCollideBox);
        Assert.Equal(0x1002, sector.Flags);
        Assert.Equal(box, sector.CollideBox);
        command.Apply();
        Assert.All(level.Sectors, s => Assert.False(s.HasCollideBox));
    }

    [Fact]
    public void DegeneratePlaneAndDanglingVerticesAreActionableNoOps()
    {
        var (level, sector) = Box();
        var zero = new CleaveSectorCommand(level, sector, Vec3.Zero, Vec3.Zero);
        zero.Apply();
        Assert.False(zero.Succeeded);
        Assert.Contains("nonzero", zero.FailureReason);
        sector.Surfaces[0].Corners[0].Vertex = new Vertex(Vec3.Zero);
        var dangling = new CleaveSectorCommand(level, sector, new Vec3(1, 0, 0), Vec3.Zero);
        dangling.Apply();
        Assert.False(dangling.Succeeded);
        Assert.Contains("vertex reference", dangling.FailureReason);
        Assert.Single(level.Sectors);
    }

    [Fact]
    public void ConnectFailureRollsBackEarlierCutsAndRetainsBothSectors()
    {
        var (level, sector) = Box();
        var other = SectorFactory.CreateBox(level, new Vec3(1, 1, 0), 1, "other.mat", 0);
        level.Sectors.Add(other);
        var sectors = level.Sectors.ToArray();
        var vertices = sectors.Select(s => s.Vertices.ToArray()).ToArray();
        var command = new ConnectSectorsCommand(level, sector, other);
        command.Apply();
        Assert.NotNull(command.Failure);
        Assert.Contains("portal", command.Failure);
        Assert.Equal(sectors, level.Sectors);
        for (int i = 0; i < sectors.Length; i++)
        {
            Assert.Equal(vertices[i], sectors[i].Vertices);
            Assert.All(sectors[i].Vertices, v => Assert.Same(sectors[i], v.Sector));
            Assert.All(sectors[i].Surfaces, s => Assert.Null(s.Adjoin));
        }
        command.Revert();
        command.Apply();
        Assert.Equal(sectors, level.Sectors);
    }

    [Fact]
    public void ConnectRetainsOverlapThingsAndUndoRestoresTheirOriginalSector()
    {
        var (level, sector) = Box();
        var other = SectorFactory.CreateBox(level, new Vec3(1, 0, 0), 1, "other.mat", 0);
        level.Sectors.Add(other);
        var overlapThing = level.NewThing(); overlapThing.Sector = other; overlapThing.Position = new Vec3(.5, 0, 0);
        var remainderThing = level.NewThing(); remainderThing.Sector = other; remainderThing.Position = new Vec3(1.5, 0, 0);
        var command = new ConnectSectorsCommand(level, sector, other);
        command.Apply();
        Assert.Null(command.Failure);
        Assert.Same(sector, overlapThing.Sector);
        Assert.NotSame(other, remainderThing.Sector);
        Assert.All(level.Things, t => Assert.Contains(t.Sector, level.Sectors));
        command.Revert();
        Assert.All(level.Things, t => Assert.Same(other, t.Sector));
        command.Apply();
        Assert.Same(sector, overlapThing.Sector);
        Assert.All(level.Things, t => Assert.Contains(t.Sector, level.Sectors));
    }
}

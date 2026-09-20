using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Query;
using Xunit;

namespace Sed.Core.Tests;

public sealed class SpatialSelectionTests
{
    private static Sector Box(Level level, Vec3? center = null, double half = 1)
    {
        var sector = SectorFactory.CreateBox(level, center ?? Vec3.Zero, half, "test.mat", 0);
        level.Sectors.Add(sector);
        return sector;
    }

    private static Sector Prism(Level level, params (double X, double Y)[] outline)
    {
        var sector = level.NewSector();
        var bottom = outline.Select(p => sector.AddVertex(new(p.X, p.Y, -1))).ToArray();
        var top = outline.Select(p => sector.AddVertex(new(p.X, p.Y, 1))).ToArray();
        AddFace(sector, bottom);
        AddFace(sector, top.Reverse().ToArray());
        for (int i = 0; i < outline.Length; i++)
        {
            int next = (i + 1) % outline.Length;
            AddFace(sector, bottom[next], bottom[i], top[i], top[next]);
        }
        return sector;
    }

    private static void AddFace(Sector sector, params Vertex[] vertices)
    {
        var face = sector.NewSurface();
        foreach (var vertex in vertices) face.Corners.Add(new() { Vertex = vertex });
    }

    [Fact]
    public void SectorInteriorUsesCurrentGeometryAndLeavesNormalCachesUntouched()
    {
        var level = new Level(); var sector = Box(level);
        sector.BoundBox = new(new(20, 20, 20), new(30, 30, 30));
        sector.Center = new(25, 25, 25);
        Assert.All(sector.Surfaces, s => Assert.Equal(Vec3.Zero, s.Normal));
        var match = Assert.Single(SpatialSelection.SelectSectors(level, Vec3.Zero));
        Assert.Same(sector, match.Sector);
        Assert.Equal(0, match.SectorIndex);
        Assert.Equal(new(-1, -1, -1), match.GeometryBounds.Min);
        Assert.Equal(new(1, 1, 1), match.GeometryBounds.Max);
        Assert.Equal(Vec3.Zero, match.BoundsCenter);
        Assert.Equal(Vec3.Zero, match.VertexCentroid);
        Assert.False(match.OnBoundary);
        Assert.Empty(SpatialSelection.SelectSectors(level, new(25, 25, 25)));
        Assert.All(sector.Surfaces, s => Assert.Equal(Vec3.Zero, s.Normal));
    }

    [Theory]
    [InlineData(0.99999995, 1, true)]
    [InlineData(1.0, 1, true)]
    [InlineData(1.00000005, 1, true)]
    [InlineData(1.0000002, 0, false)]
    [InlineData(0.99, 1, false)]
    public void SectorBoundaryToleranceIsExplicit(double x, int count, bool boundary)
    {
        var level = new Level(); Box(level);
        var matches = SpatialSelection.SelectSectors(level, new(x, 0, 0));
        Assert.Equal(count, matches.Count);
        if (count != 0) Assert.Equal(boundary, matches[0].OnBoundary);
    }

    [Fact]
    public void PortalPointAndOverlappingRoomsReturnEveryCandidate()
    {
        var level = new Level(); var a = Box(level); var b = Box(level, new(2, 0, 0));
        var first = Assert.Single(SpatialSelection.SelectSurfaces(a, new(1, 0, 0))).Surface;
        var second = Assert.Single(SpatialSelection.SelectSurfaces(b, new(-1, 0, 0))).Surface;
        first.Adjoin = second; second.Adjoin = first;
        var portal = SpatialSelection.SelectSectors(level, new(1, 0, 0));
        Assert.Equal(new[] { a, b }, portal.Select(m => m.Sector));
        Assert.All(portal, match => Assert.True(match.OnBoundary));
        var overlap = Box(level);
        Assert.Equal(new[] { a, overlap }, SpatialSelection.SelectSectors(level, Vec3.Zero).Select(m => m.Sector));
    }

    [Fact]
    public void RotatedConvexRoomUsesPlanesRatherThanItsAxisAlignedBounds()
    {
        var level = new Level(); var sector = Box(level);
        double c = System.Math.Sqrt(.5);
        foreach (var v in sector.Vertices)
            v.Position = new((v.Position.X - v.Position.Y) * c, (v.Position.X + v.Position.Y) * c, v.Position.Z);
        Assert.Single(SpatialSelection.SelectSectors(level, new(.9, 0, 0)));
        Assert.Empty(SpatialSelection.SelectSectors(level, new(1.3, 1.3, 0)));
        var wall = Assert.Single(SpatialSelection.SelectSurfaces(sector, new(c, c, 0)));
        Assert.True(wall.OutwardNormal.Dot(new(c, c, 0)) > .999999999);
    }

    [Fact]
    public void OutwardDirectionAndFacePointUseActualPolygonAndDoNotMutateCaches()
    {
        var level = new Level(); var sector = Box(level);
        var wall = Assert.Single(SpatialSelection.SelectSurfaces(sector, new(200, 0, 0), surfacePoint: new(1, .3, .2)));
        Assert.Equal(new(1, 0, 0), wall.OutwardNormal);
        Assert.Equal(new(-1, 0, 0), wall.InwardNormal);
        Assert.Equal(new(1, 0, 0), wall.Centroid);
        Assert.False(wall.PointOnBoundary);
        Assert.Empty(SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), surfacePoint: new(1, 1.5, 0)));
        Assert.Empty(SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), surfacePoint: new(1.01, 0, 0)));
        Assert.True(Assert.Single(SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), surfacePoint: new(1, 1, 0))).PointOnBoundary);
        Assert.Null(Assert.Single(SpatialSelection.SelectSurfaces(sector, new(1, 0, 0))).PointOnBoundary);
        Assert.All(sector.Surfaces, face => Assert.Equal(Vec3.Zero, face.Normal));
    }

    [Fact]
    public void CoplanarSplitFacesRequirePositionToDisambiguateAndKeepEdgeAmbiguity()
    {
        var level = new Level();
        var sector = Prism(level, (-1, -1), (1, -1), (1, 0), (1, 1), (-1, 1));
        var matches = SpatialSelection.SelectSurfaces(sector, new(1, 0, 0));
        Assert.Equal(2, matches.Count);
        var lower = Assert.Single(SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), surfacePoint: new(1, -.5, 0)));
        var upper = Assert.Single(SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), surfacePoint: new(1, .5, 0)));
        Assert.NotSame(lower.Surface, upper.Surface);
        Assert.Equal(2, SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), surfacePoint: new(1, 0, 0)).Count);
    }

    [Fact]
    public void AngularThresholdIsIndependentOfLinearToleranceAndDirectionMagnitude()
    {
        var level = new Level(); var sector = Box(level);
        double radians = .5 * System.Math.PI / 180;
        var direction = new Vec3(System.Math.Cos(radians), System.Math.Sin(radians), 0);
        Assert.Empty(SpatialSelection.SelectSurfaces(sector, direction, .4));
        Assert.Single(SpatialSelection.SelectSurfaces(sector, direction, .6));
        Assert.Single(SpatialSelection.SelectSurfaces(sector, new(double.MaxValue, 0, 0), 0));
        Assert.Single(SpatialSelection.SelectSurfaces(sector, new(double.Epsilon, 0, 0), 0));
    }

    [Theory]
    [InlineData(1e-5, 0)]
    [InlineData(1, 1000000)]
    public void SmallAndTranslatedRoomsRemainSelectable(double half, double offset)
    {
        var level = new Level(); var center = new Vec3(offset, offset, offset);
        Box(level, center, half);
        Assert.Single(SpatialSelection.SelectSectors(level, center));
    }

    [Fact]
    public void NonconvexAndOpenCandidatesRefuseInsteadOfApproximateContainment()
    {
        var level = new Level();
        Prism(level, (-1, -1), (1, -1), (1, 0), (0, 0), (0, 1), (-1, 1));
        Assert.Throws<InvalidOperationException>(() => SpatialSelection.SelectSectors(level, new(-.5, -.5, 0)));
        // A safely bounded distant unsupported sector cannot contain this point.
        Assert.Empty(SpatialSelection.SelectSectors(level, new(20, 20, 20)));
        var otherLevel = new Level(); var open = Box(otherLevel); open.Surfaces.RemoveAt(0);
        Assert.Contains("not closed", Assert.Throws<InvalidOperationException>(() => SpatialSelection.SelectSectors(otherLevel, Vec3.Zero)).Message);
    }

    [Theory]
    [InlineData("reversed")]
    [InlineData("nonplanar")]
    [InlineData("zero-edge")]
    [InlineData("repeated-corner")]
    [InlineData("unused-vertex")]
    [InlineData("duplicate-surface")]
    [InlineData("duplicate-vertex")]
    [InlineData("wrong-surface-owner")]
    [InlineData("wrong-vertex-owner")]
    [InlineData("dangling-corner")]
    public void MalformedPotentialCandidatesRefuseWithSectorContext(string fault)
    {
        var level = new Level(); var sector = Box(level);
        switch (fault)
        {
            case "reversed": sector.Surfaces[0].Corners.Reverse(); break;
            case "nonplanar": sector.Vertices[0].Position += new Vec3(.1, .2, .3); break;
            case "zero-edge": sector.Vertices[0].Position = sector.Vertices[1].Position; break;
            case "repeated-corner": sector.Surfaces[0].Corners.Add(sector.Surfaces[0].Corners[0]); break;
            case "unused-vertex": sector.AddVertex(Vec3.Zero); break;
            case "duplicate-surface": sector.Surfaces.Add(sector.Surfaces[0]); break;
            case "duplicate-vertex": sector.Vertices.Add(sector.Vertices[0]); break;
            case "wrong-surface-owner": sector.Surfaces[0].Sector = new(level); break;
            case "wrong-vertex-owner": sector.Vertices[0].Sector = new(level); break;
            case "dangling-corner": sector.Surfaces[0].Corners[0].Vertex = new(new(10, 10, 10)); break;
        }
        Assert.Contains("sector 0", Assert.Throws<InvalidOperationException>(() => SpatialSelection.SelectSectors(level, Vec3.Zero)).Message);
    }

    [Fact]
    public void DisconnectedCoincidentShellsAreNotOneSupportedBoundary()
    {
        var level = new Level(); var sector = Box(level);
        var shell = SectorFactory.CreateBox(level, Vec3.Zero, 1, "test.mat", 0);
        foreach (var vertex in shell.Vertices) { vertex.Sector = sector; sector.Vertices.Add(vertex); }
        foreach (var face in shell.Surfaces) { face.Sector = sector; sector.Surfaces.Add(face); }
        Assert.Contains("disconnected", Assert.Throws<InvalidOperationException>(() => SpatialSelection.SelectSectors(level, Vec3.Zero)).Message);
    }

    [Fact]
    public void UnknownBoundsAndHiddenDanglingCornersCannotBeCulledAway()
    {
        var level = new Level(); Box(level); var distant = Box(level, new(100, 100, 100));
        distant.Vertices[0].Position = new(double.NaN, 100, 100);
        Assert.Contains("bounds are unknown", Assert.Throws<InvalidOperationException>(() => SpatialSelection.SelectSectors(level, Vec3.Zero)).Message);
        distant.Vertices[0].Position = new(99, 99, 99);
        distant.Surfaces[0].Corners[0].Vertex = new(Vec3.Zero);
        Assert.Contains("corner", Assert.Throws<InvalidOperationException>(() => SpatialSelection.SelectSectors(level, Vec3.Zero)).Message);
    }

    [Fact]
    public void DuplicateOrDetachedSectorsAndDanglingAdjoinsRefuse()
    {
        var level = new Level(); var sector = Box(level);
        var detached = SectorFactory.CreateBox(level, Vec3.Zero, 1, "test.mat", 0);
        Assert.Throws<InvalidOperationException>(() => SpatialSelection.SelectSurfaces(detached, new(1, 0, 0)));
        sector.Surfaces[0].Adjoin = detached.Surfaces[0];
        Assert.Contains("adjoin", Assert.Throws<InvalidOperationException>(() => SpatialSelection.SectorGeometryDigest(sector)).Message);
        sector.Surfaces[0].Adjoin = null;
        level.Sectors.Add(sector);
        Assert.Throws<InvalidOperationException>(() => SpatialSelection.SelectSectors(level, Vec3.Zero));
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(0)]
    [InlineData(1e-10)] [InlineData(.01)]
    public void InvalidLinearTolerancesRefuse(double tolerance)
    {
        var level = new Level(); var sector = Box(level);
        Assert.Throws<ArgumentOutOfRangeException>(() => SpatialSelection.SelectSectors(level, Vec3.Zero, tolerance));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), tolerance: tolerance));
    }

    [Fact]
    public void NonfinitePointsZeroDirectionsAndInvalidAnglesRefuse()
    {
        var level = new Level(); var sector = Box(level);
        Assert.Throws<ArgumentException>(() => SpatialSelection.SelectSectors(level, new(double.NaN, 0, 0)));
        Assert.Throws<ArgumentException>(() => SpatialSelection.SelectSurfaces(sector, Vec3.Zero));
        Assert.Throws<ArgumentException>(() => SpatialSelection.SelectSurfaces(sector, new(0, double.PositiveInfinity, 0)));
        Assert.Throws<ArgumentException>(() => SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), surfacePoint: new(0, 0, double.NaN)));
        foreach (double angle in new[] { -1, 5.01, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => SpatialSelection.SelectSurfaces(sector, new(1, 0, 0), angle));
    }

    [Fact]
    public void DigestsAreDeterministicAndIgnoreMetadataCachesAndUnrelatedAppends()
    {
        var level = new Level(); var sector = Box(level); var face = sector.Surfaces[0];
        string sectorHash = SpatialSelection.SectorGeometryDigest(sector);
        string faceHash = SpatialSelection.SurfaceGeometryDigest(face);
        Assert.Matches("^[a-f0-9]{64}$", sectorHash);
        var sameLevel = new Level(); var sameSector = Box(sameLevel);
        Assert.Equal(sectorHash, SpatialSelection.SectorGeometryDigest(sameSector));
        sector.Num = 123; sector.Id = 456; face.Num = 99; sector.Flags = 16;
        sector.Center = new(100, 100, 100); sector.Ambient = ColorF.White;
        face.Material = "different.mat"; face.SurfFlags = 123; face.AdjoinFlags = 456;
        face.Corners[0].Uv = new(17, 23); face.Corners[0].Intensity = ColorF.Black;
        face.RecalcNormal(); sector.Vertices[0].SourceIndex = 50;
        Box(level, new(10, 0, 0));
        Assert.Equal(sectorHash, SpatialSelection.SectorGeometryDigest(sector));
        Assert.Equal(faceHash, SpatialSelection.SurfaceGeometryDigest(face));
        Assert.NotEqual(faceHash, SpatialSelection.SurfaceGeometryDigest(sector.Surfaces[1]));
        Assert.NotEqual(faceHash, sectorHash);
    }

    [Fact]
    public void SurfaceDigestBindsOtherSectorBoundariesAndCornerOrder()
    {
        var level = new Level(); var sector = Box(level);
        var east = Assert.Single(SpatialSelection.SelectSurfaces(sector, new(1, 0, 0))).Surface;
        string original = SpatialSelection.SurfaceGeometryDigest(east);
        var westVertex = sector.Vertices.First(v => v.Position.X == -1);
        westVertex.Position += new Vec3(-.1, 0, 0);
        Assert.NotEqual(original, SpatialSelection.SurfaceGeometryDigest(east));
        westVertex.Position += new Vec3(.1, 0, 0);
        string beforeOrder = SpatialSelection.SurfaceGeometryDigest(east);
        var corner = east.Corners[0]; east.Corners.RemoveAt(0); east.Corners.Add(corner);
        Assert.NotEqual(beforeOrder, SpatialSelection.SurfaceGeometryDigest(east));
    }

    [Fact]
    public void DigestBindsReciprocalAdjoinEndpointsAndExactPositionBits()
    {
        var level = new Level(); var a = Box(level); var b = Box(level, new(2, 0, 0));
        var fa = Assert.Single(SpatialSelection.SelectSurfaces(a, new(1, 0, 0))).Surface;
        var fb = Assert.Single(SpatialSelection.SelectSurfaces(b, new(-1, 0, 0))).Surface;
        string original = SpatialSelection.SectorGeometryDigest(a);
        fa.Adjoin = fb; fb.Adjoin = fa;
        Assert.NotEqual(original, SpatialSelection.SectorGeometryDigest(a));
        fb.Adjoin = null;
        Assert.Throws<InvalidOperationException>(() => SpatialSelection.SurfaceGeometryDigest(fa));
        fa.Adjoin = null;
        a.Vertices[0].Position = new(0.0, -1, -1);
        string positiveZero = SpatialSelection.SectorGeometryDigest(a);
        a.Vertices[0].Position = new(BitConverter.Int64BitsToDouble(long.MinValue), -1, -1);
        Assert.NotEqual(positiveZero, SpatialSelection.SectorGeometryDigest(a));
    }
}

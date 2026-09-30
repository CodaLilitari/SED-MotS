using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Xunit;

namespace Sed.Core.Tests;

public sealed class BatchSurfaceUvTests
{
    // Original analytical planes: U cross V must equal the requested directed
    // normal. The projection must measure 2 and 3 world units as 14 and 21 texels.
    public static IEnumerable<object[]> DirectedBases()
    {
        yield return [SurfaceUvProjectionDirection.PositiveX, new Vec3(0, 1, 0), new Vec3(0, 0, 1), new Vec3(1, 0, 0)];
        yield return [SurfaceUvProjectionDirection.NegativeX, new Vec3(0, -1, 0), new Vec3(0, 0, 1), new Vec3(-1, 0, 0)];
        yield return [SurfaceUvProjectionDirection.PositiveY, new Vec3(-1, 0, 0), new Vec3(0, 0, 1), new Vec3(0, 1, 0)];
        yield return [SurfaceUvProjectionDirection.NegativeY, new Vec3(1, 0, 0), new Vec3(0, 0, 1), new Vec3(0, -1, 0)];
        yield return [SurfaceUvProjectionDirection.PositiveZ, new Vec3(1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, 1)];
        yield return [SurfaceUvProjectionDirection.NegativeZ, new Vec3(-1, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 0, -1)];
    }

    [Theory]
    [MemberData(nameof(DirectedBases))]
    public void SixDirectedBases_HaveDocumentedHandednessAndFixedScale(
        SurfaceUvProjectionDirection direction, Vec3 u, Vec3 v, Vec3 expectedNormal)
    {
        var origin = new Vec3(5, -7, 11);
        var surface = MakeSurface(new Level(), origin, origin + 2 * u, origin + 2 * u + 3 * v, origin + 3 * v);
        Assert.Equal(expectedNormal, surface.CalculateNormal());
        BatchSurfaceUvCommand.Project([surface], direction, origin, 7).Apply();
        AssertUvs(surface, new(0, 0), new(14, 0), new(14, 21), new(0, 21));
    }

    [Fact]
    public void CommonProjection_SeamsMatchAcrossIndependentSectorsAndDifferentFaceSizes()
    {
        var level = new Level();
        var left = MakeSurface(level, new(0, 0, 3), new(2, 0, 3), new(2, 2, 3), new(0, 2, 3));
        var right = MakeSurface(level, new(2, 0, 3), new(7, 0, 3), new(7, 2, 3), new(2, 2, 3));
        BatchSurfaceUvCommand.Project([left, right], SurfaceUvProjectionDirection.PositiveZ, new(1, -1, 0), 8).Apply();
        AssertUvs(left, new(-8, 8), new(8, 8), new(8, 24), new(-8, 24));
        AssertUvs(right, new(8, 8), new(48, 8), new(48, 24), new(8, 24));
        Assert.Equal(left.Corners[1].Uv, right.Corners[0].Uv);
        Assert.Equal(left.Corners[2].Uv, right.Corners[3].Uv);
    }

    [Fact]
    public void Transform_UsesOneExplicitPivotAndDocumentedScaleRotateOffsetOrder()
    {
        var level = new Level(); var a = Square(level); var b = Square(level, 2);
        a.Corners[0].Uv = new(12, 23);
        b.Corners[0].Uv = new(14, 25);
        b.Corners[1].Uv = a.Corners[0].Uv;
        var command = BatchSurfaceUvCommand.Transform([a, b], new(10, 20), new(2, 3), 90, new(5, -4));
        command.Apply();
        Assert.Equal(6, a.Corners[0].Uv.U, 10); // (2,3) -> (4,9) -> (-9,4) + (10,20) + (5,-4)
        Assert.Equal(20, a.Corners[0].Uv.V, 10);
        Assert.Equal(0, b.Corners[0].Uv.U, 10);
        Assert.Equal(24, b.Corners[0].Uv.V, 10);
        Assert.Equal(a.Corners[0].Uv, b.Corners[1].Uv);
    }

    [Fact]
    public void BothModes_UndoRedoRestoreExactUvsAndPreserveIndependentLightingAndSurfaceState()
    {
        foreach (bool project in new[] { false, true })
        {
            var level = new Level(); var a = Square(level); var b = Square(level, 2);
            var surfaces = new[] { a, b };
            a.Corners[0].Uv = new(BitConverter.Int64BitsToDouble(long.MinValue), double.Epsilon);
            foreach (var s in surfaces)
            {
                s.Material = "original.mat"; s.MaterialIndex = 7; s.SurfFlags = 0x420; s.FaceFlags = 0x42;
                s.AdjoinFlags = 9; s.Geo = 3; s.Light = 1; s.Tex = 2; s.ExtraLightIntensity = .71f;
                s.UScale = 1.25f; s.VScale = 3.5f; s.RecalcNormal();
                for (int i = 0; i < s.Corners.Count; i++)
                {
                    s.Corners[i].Intensity = new ColorF(.11f + i, .23f + i, .47f + i);
                    s.Corners[i].MonoIntensity = i == 0 ? null : .83f + i;
                }
            }
            a.Adjoin = b; b.Adjoin = a;
            var before = surfaces.Select(s => s.Corners.Select(c => c.Uv).ToArray()).ToArray();
            var positions = surfaces.Select(s => s.Corners.Select(c => c.Vertex.Position).ToArray()).ToArray();
            var command = project
                ? BatchSurfaceUvCommand.Project(surfaces, SurfaceUvProjectionDirection.NegativeZ, new(.25, -.75, 0), 32)
                : BatchSurfaceUvCommand.Transform(surfaces, new(4, 7), new(-2, .5), 35, new(3, -1));
            command.Apply();
            var after = surfaces.Select(s => s.Corners.Select(c => c.Uv).ToArray()).ToArray();
            command.Revert();
            for (int s = 0; s < surfaces.Length; s++) AssertExactUvs(before[s], surfaces[s]);
            command.Apply();
            for (int s = 0; s < surfaces.Length; s++)
            {
                var surface = surfaces[s];
                AssertExactUvs(after[s], surface);
                Assert.Equal(positions[s], surface.Corners.Select(c => c.Vertex.Position));
                Assert.Equal(new Vec3(0, 0, 1), surface.Normal);
                Assert.Equal("original.mat", surface.Material); Assert.Equal(7, surface.MaterialIndex);
                Assert.Equal(0x420, surface.SurfFlags); Assert.Equal(0x42, surface.FaceFlags);
                Assert.Equal(9, surface.AdjoinFlags); Assert.Equal(3, surface.Geo); Assert.Equal(1, surface.Light); Assert.Equal(2, surface.Tex);
                Assert.Equal(.71f, surface.ExtraLightIntensity); Assert.Equal(1.25f, surface.UScale); Assert.Equal(3.5f, surface.VScale);
                Assert.Same(s == 0 ? b : a, surface.Adjoin);
                for (int c = 0; c < surface.Corners.Count; c++)
                {
                    Assert.Equal(new ColorF(.11f + c, .23f + c, .47f + c), surface.Corners[c].Intensity);
                    Assert.Equal(c == 0 ? (float?)null : .83f + c, surface.Corners[c].MonoIntensity);
                }
            }
        }
    }

    [Fact]
    public void InvalidFinalSurface_NeverMutatesEarlierSurface()
    {
        var level = new Level(); var first = Square(level); var last = Square(level, 2);
        var before = first.Corners.Select(c => c.Uv).ToArray();
        last.Corners[3].Uv = new(double.NaN, 0);
        Assert.Throws<ArgumentException>(() => BatchSurfaceUvCommand.Project([first, last], SurfaceUvProjectionDirection.PositiveZ, Vec3.Zero, 64));
        AssertExactUvs(before, first);
    }

    [Fact]
    public void EdgeOnFinalFace_RefusesWholeProjection()
    {
        var level = new Level(); var first = Square(level);
        var edgeOn = MakeSurface(level, new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1));
        var before = first.Corners.Select(c => c.Uv).ToArray();
        Assert.Throws<ArgumentException>(() => BatchSurfaceUvCommand.Project([first, edgeOn], SurfaceUvProjectionDirection.PositiveZ, Vec3.Zero, 64));
        AssertExactUvs(before, first);
    }

    [Fact]
    public void SameCountCornerReplacementInFinalFace_RefusesApplyBeforeAnyMutation()
    {
        var level = new Level(); var first = Square(level); var last = Square(level, 2);
        var before = first.Corners.Select(c => c.Uv).ToArray();
        var command = BatchSurfaceUvCommand.Project([first, last], SurfaceUvProjectionDirection.PositiveZ, Vec3.Zero, 64);
        last.Corners[3] = new Surface.Corner { Vertex = last.Corners[3].Vertex, Uv = last.Corners[3].Uv };
        Assert.Throws<InvalidOperationException>(() => command.Apply());
        AssertExactUvs(before, first);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleFinalFaceGeometryOrUv_RefusesUndoAtomically(bool geometry)
    {
        var level = new Level(); var first = Square(level); var last = Square(level, 2);
        var command = BatchSurfaceUvCommand.Project([first, last], SurfaceUvProjectionDirection.PositiveZ, Vec3.Zero, 64);
        command.Apply();
        var after = first.Corners.Select(c => c.Uv).ToArray();
        if (geometry) last.Corners[3].Vertex.Position += new Vec3(0, 0, 1);
        else last.Corners[3].Uv = new(123, 456);
        Assert.Throws<InvalidOperationException>(() => command.Revert());
        AssertExactUvs(after, first);
    }

    [Fact]
    public void TopologyMembershipDuplicateSelectionsAndSharedCornerObjectsAreRefused()
    {
        var level = new Level(); var a = Square(level); var b = Square(level, 2);
        Assert.Throws<ArgumentException>(() => Project([a, a]));
        Assert.Throws<ArgumentException>(() => Project([a, Square(new Level())]));
        var vertex = b.Corners[3].Vertex;
        b.Sector.Vertices.Remove(vertex);
        Assert.Throws<ArgumentException>(() => Project([a, b]));
        b.Sector.Vertices.Add(vertex);
        b.Corners[3] = b.Corners[0];
        Assert.Throws<ArgumentException>(() => Project([a, b]));
        a.Sector.Surfaces.Remove(a);
        Assert.Throws<ArgumentException>(() => Project([a]));
    }

    [Fact]
    public void NonplanarIntersectingAndZeroLengthGeometryAreRefused()
    {
        var level = new Level();
        var nonplanar = MakeSurface(level, new(0, 0, 0), new(1, 0, 0), new(1, 1, .1), new(0, 1, 0));
        var crossed = MakeSurface(level, new(0, 0, 0), new(3, 2, 0), new(0, 2, 0), new(2, 0, 0));
        var zeroEdge = MakeSurface(level, new(0, 0, 0), new(1, 0, 0), new(1, 0, 0), new(0, 1, 0));
        foreach (var surface in new[] { nonplanar, crossed, zeroEdge })
            Assert.Throws<ArgumentException>(() => Project([surface]));
    }

    [Fact]
    public void TranslationFarFromWorldOriginDoesNotCauseFalseDegenerateGeometry()
    {
        var origin = new Vec3(1e12, -1e12, 0);
        var surface = MakeSurface(new Level(), origin, origin + new Vec3(2, 0, 0), origin + new Vec3(2, 3, 0), origin + new Vec3(0, 3, 0));
        BatchSurfaceUvCommand.Project([surface], SurfaceUvProjectionDirection.PositiveZ, origin, 16).Apply();
        AssertUvs(surface, new(0, 0), new(32, 0), new(32, 48), new(0, 48));
    }

    [Fact]
    public void CollinearBoundaryCornerIsPreservedWithoutChangingCachedNormal()
    {
        var surface = MakeSurface(new Level(), new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 1, 0), new(0, 1, 0));
        var corners = surface.Corners.ToArray(); var normal = surface.Normal;
        Project([surface]).Apply();
        Assert.Equal(corners, surface.Corners);
        Assert.Equal(normal, surface.Normal);
        AssertUvs(surface, new(0, 0), new(64, 0), new(128, 0), new(128, 64), new(0, 64));
    }

    [Fact]
    public void NumericalCollapseAndOutputsOutsideWriterBoundsAreRefused()
    {
        var surface = Square(new Level());
        Assert.Throws<ArgumentException>(() => BatchSurfaceUvCommand.Project([surface], SurfaceUvProjectionDirection.PositiveZ, new(1e20, 1e20, 0), 1e-20));
        Assert.Throws<ArgumentException>(() => Project([surface], 1_000_001));
        Assert.Throws<ArgumentException>(() => BatchSurfaceUvCommand.Transform([surface], new(0, 0), new(1, 1), 0, new(1e6, 0)));
        Project([surface], 1_000_000).Apply();
        AssertUvs(surface, new(0, 0), new(1e6, 0), new(1e6, 1e6), new(0, 1e6));
    }

    [Fact]
    public void FiniteParameterAndVertexValidationIsAtomic()
    {
        var surface = Square(new Level()); var before = surface.Corners.Select(c => c.Uv).ToArray();
        Assert.Throws<ArgumentException>(() => BatchSurfaceUvCommand.Project([surface], (SurfaceUvProjectionDirection)99, Vec3.Zero, 1));
        Assert.Throws<ArgumentException>(() => BatchSurfaceUvCommand.Project([surface], SurfaceUvProjectionDirection.PositiveZ, new(0, double.NaN, 0), 1));
        foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentException>(() => Project([surface], invalid));
        Assert.Throws<ArgumentException>(() => BatchSurfaceUvCommand.Transform([surface], new(0, 0), new(0, 1), 0, new(0, 0)));
        Assert.Throws<ArgumentException>(() => BatchSurfaceUvCommand.Transform([surface], new(0, 0), new(1, 1), double.PositiveInfinity, new(0, 0)));
        surface.Corners[3].Vertex.Position = new(0, 1, double.NaN);
        Assert.Throws<ArgumentException>(() => Project([surface]));
        AssertExactUvs(before, surface);
    }

    [Fact]
    public void SelectionAndCornerBudgetsAreEnforcedBeforeMutation()
    {
        var level = new Level(); var first = Square(level); var before = first.Corners.Select(c => c.Uv).ToArray();
        Assert.Throws<ArgumentException>(() => Project([]));
        Assert.Throws<ArgumentException>(() => Project(Enumerable.Repeat(first, BatchSurfaceUvCommand.MaxSurfaces + 1).ToArray()));
        var tooManyCorners = RegularPolygon(level, BatchSurfaceUvCommand.MaxCornersPerSurface + 1);
        Assert.Throws<ArgumentException>(() => Project([first, tooManyCorners]));
        var selection = Enumerable.Range(0, BatchSurfaceUvCommand.MaxTotalCorners / 64 + 1)
            .Select(_ => RegularPolygon(level, 64)).ToArray();
        Assert.Throws<ArgumentException>(() => Project(selection));
        AssertExactUvs(before, first);
    }

    private static BatchSurfaceUvCommand Project(IReadOnlyList<Surface> surfaces, double scale = 64)
        => BatchSurfaceUvCommand.Project(surfaces, SurfaceUvProjectionDirection.PositiveZ, Vec3.Zero, scale);

    private static Surface Square(Level level, double x = 0)
        => MakeSurface(level, new(x, 0, 0), new(x + 1, 0, 0), new(x + 1, 1, 0), new(x, 1, 0));

    private static Surface RegularPolygon(Level level, int corners)
        => MakeSurface(level, Enumerable.Range(0, corners).Select(i => new Vec3(System.Math.Cos(i * 2 * System.Math.PI / corners),
            System.Math.Sin(i * 2 * System.Math.PI / corners), 0)).ToArray());

    private static Surface MakeSurface(Level level, params Vec3[] positions)
    {
        var sector = level.NewSector(); var surface = sector.NewSurface();
        for (int i = 0; i < positions.Length; i++)
            surface.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(positions[i]), Uv = new(2.5 + i * 3, -1.25 + i * 5) });
        surface.RecalcNormal();
        return surface;
    }

    private static void AssertUvs(Surface surface, params TexVertex[] expected)
    {
        Assert.Equal(expected.Length, surface.Corners.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].U, surface.Corners[i].Uv.U, 10);
            Assert.Equal(expected[i].V, surface.Corners[i].Uv.V, 10);
        }
    }

    private static void AssertExactUvs(TexVertex[] expected, Surface surface)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].U), BitConverter.DoubleToInt64Bits(surface.Corners[i].Uv.U));
            Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].V), BitConverter.DoubleToInt64Bits(surface.Corners[i].Uv.V));
        }
    }
}

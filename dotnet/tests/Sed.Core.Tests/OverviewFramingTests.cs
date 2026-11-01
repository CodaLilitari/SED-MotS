using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Rendering;

namespace Sed.Core.Tests;

/// <summary>
/// Wren framing (SPEC §2.1), shared with tools/blender: golden numbers for the box
/// (-10,-5,0)..(10,5,4) from the planner's reference script, asserted to 1e-9.
/// </summary>
public sealed class OverviewFramingTests
{
    private static readonly Box Golden = new(new Vec3(-10, -5, 0), new Vec3(10, 5, 4));
    private const double Tolerance = 1e-9;

    private static void Near(double expected, double actual, string what) =>
        Assert.True(System.Math.Abs(expected - actual) <= Tolerance, $"{what}: expected {expected:R}, got {actual:R}");
    private static void Near(Vec3 expected, Vec3 actual, string what)
    {
        Near(expected.X, actual.X, what + ".x"); Near(expected.Y, actual.Y, what + ".y"); Near(expected.Z, actual.Z, what + ".z");
    }

    [Theory]
    [InlineData("wren")]
    [InlineData("contain")]
    public void OverheadLandscapeMatchesTheGoldenPlan(string framing)
    {
        var camera = OverviewFraming.Compute(Golden, "overhead", framing, 1280, 960, 0.025);
        Assert.Equal("orthographic", camera.Projection);
        Near(new Vec3(0, 0, 24), camera.Position, "position");
        Near(new Vec3(0, 0, 2), camera.Target, "target");
        Near(15.75, camera.OrthographicHeight, "orthographicHeight");
        Near(21.0, camera.OrthographicHeight * 1280 / 960, "width");
        Near(10, camera.Near, "near");
        Near(34, camera.Far, "far");
        Assert.Equal(0, camera.FieldOfViewDegrees);
    }

    [Fact]
    public void OverheadPortraitFitsTheWidth()
    {
        var camera = OverviewFraming.Compute(Golden, "overhead", "wren", 600, 800, 0.025);
        Near(28.0, camera.OrthographicHeight, "orthographicHeight");
        Near(21.0, camera.OrthographicHeight * 600 / 800, "width");
    }

    [Fact]
    public void ThreeQuarterWrenMatchesTheGoldenCamera()
    {
        var camera = OverviewFraming.Compute(Golden, "threequarter", "wren", 1280, 960, 0.025);
        Assert.Equal("perspective", camera.Projection);
        Near(new Vec3(11.925707526180574, -11.925707526180574, 12.222035022440492), camera.Position, "position");
        Near(new Vec3(0, 0, 2), camera.Target, "target");
        Near(42.18467901165499, camera.FieldOfViewDegrees, "fovY");
        Near(4.181809173731455, camera.Near, "near");
        Near(31.192829897580012, camera.Far, "far");
        Assert.Equal(0, camera.OrthographicHeight);
    }

    [Fact]
    public void ThreeQuarterContainMatchesTheGoldenCamera()
    {
        var camera = OverviewFraming.Compute(Golden, "threequarter", "contain", 1280, 960, 0.025);
        Near(new Vec3(16.75338631061249, -16.75338631061249, 16.360045409096422), camera.Position, "position");
        Near(42.18467901165499, camera.FieldOfViewDegrees, "fovY");
        Near(8.1735536185632, camera.Near, "near");
        Near(39.1763187872435, camera.Far, "far");
    }

    [Theory]
    [InlineData("wren")]
    [InlineData("contain")]
    public void ThreeQuarterPortraitUsesTheTallerFieldOfView(string framing) =>
        Near(54.43222311461495, OverviewFraming.Compute(Golden, "threequarter", framing, 600, 800, 0.025).FieldOfViewDegrees, "fovY");

    [Theory]
    [InlineData(1280, 960, 0.025)]
    [InlineData(600, 800, 0.025)]
    [InlineData(1024, 256, 0.0)]
    [InlineData(256, 1024, 0.25)]
    [InlineData(640, 640, 0.1)]
    public void ContainKeepsEveryCornerInsideTheMarginAndTouchesIt(int width, int height, double margin)
    {
        foreach (var box in new[] { Golden, new Box(new Vec3(3, -40, -2), new Vec3(9, 60, 30)), new Box(new Vec3(-1, -1, 0), new Vec3(1, 1, 0)) })
        {
            var camera = OverviewFraming.Compute(box, "threequarter", "contain", width, height, margin);
            // Double-precision projection (the Vulkan matrices are single precision).
            var forward = (camera.Target - camera.Position).Normalized();
            var right = forward.Cross(new Vec3(0, 0, 1)).Normalized();
            var up = right.Cross(forward);
            double tanV = System.Math.Tan(camera.FieldOfViewDegrees * System.Math.PI / 360), tanH = tanV * width / height;
            double bound = 1 - 2 * margin, worst = 0;
            foreach (double x in new[] { box.Min.X, box.Max.X })
                foreach (double y in new[] { box.Min.Y, box.Max.Y })
                    foreach (double z in new[] { box.Min.Z, box.Max.Z })
                    {
                        var q = new Vec3(x, y, z) - camera.Position;
                        double depth = forward.Dot(q);
                        Assert.True(depth > camera.Near, "corner in front of the near plane");
                        Assert.True(depth < camera.Far, "corner inside the far plane");
                        double ndcX = right.Dot(q) / depth / tanH, ndcY = up.Dot(q) / depth / tanV;
                        Assert.True(System.Math.Abs(ndcX) <= bound + 1e-12 && System.Math.Abs(ndcY) <= bound + 1e-12,
                            $"corner ({x},{y},{z}) at NDC ({ndcX},{ndcY}) outside ±{bound}");
                        worst = System.Math.Max(worst, System.Math.Max(System.Math.Abs(ndcX), System.Math.Abs(ndcY)));
                    }
            Near(bound, worst, "the limiting corner touches the margin");
        }
    }

    [Fact]
    public void ThreeQuarterLooksFromTheSouthEastAndAbove()
    {
        var camera = OverviewFraming.Compute(Golden, "threequarter", "contain", 800, 600, 0.025);
        var direction = (camera.Position - camera.Target).Normalized();
        Near(new Vec3(0.7, -0.7, 0.6).Normalized(), direction, "direction");
    }

    [Fact]
    public void DegenerateAndEmptyBoundsAreRefused()
    {
        var flat = new Box(new Vec3(5, 5, 0), new Vec3(5, 5, 10)); // a vertical line: nothing to frame in X/Y
        Assert.Equal("Level bounds are degenerate in X and Y; nothing to frame.",
            Assert.Throws<InvalidOperationException>(() => OverviewFraming.Compute(flat, "overhead", "wren", 640, 480, 0.025)).Message);
        Assert.Equal("Level has no drawn world surfaces to frame.",
            Assert.Throws<InvalidOperationException>(() => OverviewFraming.Compute(Box.Empty, "overhead", "wren", 640, 480, 0.025)).Message);
        // A thin but real footprint (a line in X) still frames.
        var line = OverviewFraming.Compute(new Box(new Vec3(0, 0, 0), new Vec3(10, 0, 0)), "overhead", "wren", 640, 480, 0.025);
        Assert.True(line.OrthographicHeight > 0);
        Assert.Throws<ArgumentException>(() => OverviewFraming.Compute(Golden, "side", "wren", 640, 480, 0.025));
        Assert.Throws<ArgumentException>(() => OverviewFraming.Compute(Golden, "overhead", "tight", 640, 480, 0.025));
        Assert.Throws<ArgumentException>(() => OverviewFraming.Compute(Golden, "overhead", "wren", 640, 480, 0.3));
        Assert.Throws<ArgumentException>(() => OverviewFraming.Compute(Golden, "overhead", "wren", 640, 480, double.NaN));
    }

    [Fact]
    public void WorldSurfaceBoundsUseOnlyDrawnLevelSurfaces()
    {
        var level = new Level();
        var sector = level.NewSector();
        Surface Add(string material, int geo, params Vec3[] corners)
        {
            var surface = sector.NewSurface();
            surface.Material = material;
            surface.Geo = geo;
            foreach (var p in corners) surface.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(p) });
            return surface;
        }
        Assert.Null(OverviewFraming.WorldSurfaceBounds(level));
        Add("floor.mat", 4, new Vec3(0, 0, 0), new Vec3(2, 0, 0), new Vec3(2, 3, 0));
        Add("", 4, new Vec3(-100, 0, 0), new Vec3(-90, 0, 0), new Vec3(-90, 5, 0));          // no material (portal)
        Add("portal.mat", 0, new Vec3(0, 200, 0), new Vec3(5, 200, 0), new Vec3(5, 210, 0)); // geo 0
        Add("edge.mat", 4, new Vec3(0, 0, -50), new Vec3(0, 0, 50));                          // fewer than three corners
        var sky = Add("sky.mat", 4, new Vec3(0, 0, 7), new Vec3(1, 0, 7), new Vec3(1, 1, 7));  // sky counts
        sky.SurfFlags = Surface.SfSkyCeiling;
        var thing = level.NewThing(); // things are never framed
        thing.Position = new Vec3(500, 500, 500);
        var bounds = OverviewFraming.WorldSurfaceBounds(level);
        Assert.NotNull(bounds);
        Assert.Equal(new Vec3(0, 0, 0), bounds.Value.Min);
        Assert.Equal(new Vec3(2, 3, 7), bounds.Value.Max);
        Assert.True(SceneAssembler.IsDrawnWorldSurface(sky));
        Assert.False(SceneAssembler.IsDrawnWorldSurface(sector.Surfaces[1]));
        Assert.False(SceneAssembler.IsDrawnWorldSurface(sector.Surfaces[2]));
        Assert.False(SceneAssembler.IsDrawnWorldSurface(sector.Surfaces[3]));
    }
}

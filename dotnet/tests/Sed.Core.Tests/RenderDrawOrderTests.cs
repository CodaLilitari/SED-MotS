using Sed.Core.Math;
using Sed.Rendering;

namespace Sed.Core.Tests;

public sealed class RenderDrawOrderTests
{
    [Fact]
    public void MinimumForwardDepthIgnoresOffAxisDistanceAndChangesWithCamera()
    {
        var a = new Vec3(100, 2, 0);
        var b = new Vec3(101, 4, 0);
        var c = new Vec3(100, 6, 0);
        var forward = Camera.LookingAt(Vec3.Zero, new Vec3(0, 1, 0)).ViewProjection(1);
        Assert.Equal(2, RenderDrawOrder.MinimumDepth(forward, a, b, c), 5);
        var reverse = Camera.LookingAt(new Vec3(0, 10, 0), Vec3.Zero).ViewProjection(1);
        Assert.Equal(4, RenderDrawOrder.MinimumDepth(reverse, a, b, c), 5);
    }

    [Fact]
    public void OrthographicDepthUsesNearestCornerInsteadOfCentroid()
        => Assert.Equal(.1, RenderDrawOrder.MinimumDepth(Mat4.Identity,
            new Vec3(-1, 0, .8), new Vec3(0, 1, .1), new Vec3(1, 0, .6)), 6);

    [Fact]
    public void MaskBitIsIndependentOfLightingAndNeverTurnsSolidZeroIntoAHole()
    {
        Assert.Equal(0x308u, RenderLightingState.Pack(RenderLightingMode.Mono, false, 3, hasColorKey: true));
        Assert.Equal(0x303u, RenderLightingState.Pack(RenderLightingMode.Rgb, true, 3, hasColorKey: true));
    }
}

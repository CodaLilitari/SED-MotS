using Sed.Core.Math;
using Sed.Rendering;

namespace Sed.Core.Tests;

/// <summary>Orthographic cameras and vertical views (Wren's overhead plan), without changing perspective cameras.</summary>
public sealed class CameraProjectionTests
{
    private static void Near(Vec3 expected, Vec3 actual, double tolerance = 1e-12)
    {
        Assert.True(System.Math.Abs(expected.X - actual.X) <= tolerance && System.Math.Abs(expected.Y - actual.Y) <= tolerance
            && System.Math.Abs(expected.Z - actual.Z) <= tolerance, $"expected {expected}, got ({actual.X:R}, {actual.Y:R}, {actual.Z:R})");
    }

    private static (double X, double Y, double Z) Ndc(Mat4 m, Vec3 p)
    {
        double x = m.M[0] * p.X + m.M[4] * p.Y + m.M[8] * p.Z + m.M[12];
        double y = m.M[1] * p.X + m.M[5] * p.Y + m.M[9] * p.Z + m.M[13];
        double z = m.M[2] * p.X + m.M[6] * p.Y + m.M[10] * p.Z + m.M[14];
        double w = m.M[3] * p.X + m.M[7] * p.Y + m.M[11] * p.Z + m.M[15];
        return (x / w, y / w, z / w);
    }

    [Fact]
    public void LookingStraightDownHasNorthUpAndEastRight()
    {
        var camera = Camera.LookingAt(new Vec3(0, 0, 5), Vec3.Zero);
        Assert.True(camera.IsVertical);
        Near(new Vec3(1, 0, 0), camera.Right);
        Near(new Vec3(0, 1, 0), camera.CameraUp);
        Assert.All(camera.ViewProjection(4.0 / 3).M, v => Assert.True(float.IsFinite(v)));
        camera.Projection = CameraProjection.Orthographic;
        Assert.All(camera.ViewProjection(4.0 / 3).M, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void ExactVerticalPitchUsesTheYawForItsScreenAxes()
    {
        var down = new Camera { Position = new Vec3(0, 0, 10), Pitch = -System.Math.PI / 2, Yaw = 0 };
        Near(new Vec3(1, 0, 0), down.Right);
        Near(new Vec3(0, 1, 0), down.CameraUp);
        var up = new Camera { Position = Vec3.Zero, Pitch = System.Math.PI / 2, Yaw = 0 };
        Near(new Vec3(1, 0, 0), up.Right);
        Near(new Vec3(0, -1, 0), up.CameraUp);
        var turned = new Camera { Pitch = -System.Math.PI / 2, Yaw = System.Math.PI / 2 };
        Near(new Vec3(0, -1, 0), turned.Right);
        Near(new Vec3(1, 0, 0), turned.CameraUp);
    }

    [Fact]
    public void NonVerticalCamerasKeepTheirExactPerspectiveMatrices()
    {
        // The pre-orthographic formula, element for element.
        foreach (var (eye, target, fov) in new[] { (new Vec3(3.5, -4, 2.8), Vec3.Zero, 60.0), (new Vec3(0, -3, 4), Vec3.Zero, 65.0),
                     (new Vec3(11.925707526180574, -11.925707526180574, 12.222035022440492), new Vec3(0, 0, 2), 42.18467901165499) })
        {
            var camera = Camera.LookingAt(eye, target, fov);
            camera.NearPlane = 0.02; camera.FarPlane = 77;
            Assert.False(camera.IsVertical);
            Assert.Equal(camera.Forward.Cross(Camera.Up).Normalized(), camera.Right);
            var legacy = Mat4.Perspective(fov * System.Math.PI / 180.0, 1.5, 0.02, 77) * Mat4.LookAt(camera.Position, camera.Position + camera.Forward, Camera.Up);
            Assert.Equal(legacy.M, camera.ViewProjection(1.5).M);
            Assert.Equal(new RenderEye(eye, Vec3.Zero, false), camera.Eye);
        }
    }

    [Fact]
    public void OrthographicOverheadMapsWorldPointsToTheExpectedNdc()
    {
        // Wren's golden overhead: eye (0,0,24) over target (0,0,2), 21 x 15.75 units, near 10, far 34.
        var camera = new Camera { Position = new Vec3(0, 0, 24), Pitch = -System.Math.PI / 2, Yaw = 0,
            Projection = CameraProjection.Orthographic, OrthographicHeight = 15.75, NearPlane = 10, FarPlane = 34 };
        var vp = camera.ViewProjection(1280.0 / 960);
        var east = Ndc(vp, new Vec3(10.5, 0, 2));
        Assert.Equal(1.0, east.X, 5); Assert.Equal(0.0, east.Y, 5);
        var north = Ndc(vp, new Vec3(0, 7.875, 2));
        Assert.Equal(0.0, north.X, 5); Assert.Equal(-1.0, north.Y, 5); // Vulkan NDC -Y is the top of the image
        var nearPoint = Ndc(vp, new Vec3(-10.5, -7.875, 14));
        Assert.Equal(-1.0, nearPoint.X, 5); Assert.Equal(1.0, nearPoint.Y, 5); Assert.Equal(0.0, nearPoint.Z, 4);
        Assert.Equal(1.0, Ndc(vp, new Vec3(3, 4, -10)).Z, 4);
        // Parallel rays: the same (x, y) at any height lands on the same pixel.
        var low = Ndc(vp, new Vec3(3, 4, 0)); var high = Ndc(vp, new Vec3(3, 4, 12));
        Assert.Equal(low.X, high.X, 6); Assert.Equal(low.Y, high.Y, 6);
    }

    [Fact]
    public void EyeIsAPointForPerspectiveAndADirectionForOrthographic()
    {
        var camera = Camera.LookingAt(new Vec3(4, -4, 3), Vec3.Zero);
        Assert.False(camera.Eye.Orthographic);
        Assert.Equal(camera.Position, camera.Eye.Position);
        camera.Projection = CameraProjection.Orthographic;
        var eye = camera.Eye;
        Assert.True(eye.Orthographic);
        Near(camera.Forward * -1, eye.TowardCamera);
        Assert.Equal(1.0, eye.TowardCamera.Length, 12);
        Near(new Vec3(0, 0, 1), RenderEye.Parallel(Vec3.Zero, new Vec3(0, 0, -7)).TowardCamera);
    }

    [Fact]
    public void ViewProjectionDiffersOnlyInProjectionBetweenModes()
    {
        var camera = Camera.LookingAt(new Vec3(4, -4, 3), Vec3.Zero, 50);
        camera.NearPlane = 1; camera.FarPlane = 20;
        var view = Mat4.LookAt(camera.Position, camera.Position + camera.Forward, Camera.Up);
        camera.Projection = CameraProjection.Orthographic; camera.OrthographicHeight = 6;
        Assert.Equal((Mat4.Orthographic(6 * 2.0, 6, 1, 20) * view).M, camera.ViewProjection(2.0).M);
    }
}

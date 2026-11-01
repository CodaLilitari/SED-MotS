using Sed.Core.Math;
using Xunit;

namespace Sed.Core.Tests;

public class MathTests
{
    [Fact]
    public void Cross3_MatchesOriginalFormula()
    {
        // VectorCross3 from math.pas: cx=y1*z2-z1*y2, cy=z1*x2-x1*z2, cz=x1*y2-x2*y1
        var a = new Vec3(1, 0, 0);
        var b = new Vec3(0, 1, 0);
        Assert.Equal(new Vec3(0, 0, 1), a.Cross(b));
    }

    [Fact]
    public void Normalize_ReturnsLength_AndUnitVector()
    {
        var v = new Vec3(0, 3, 4);
        var n = v.Normalized(out var len);
        Assert.Equal(5.0, len, 9);
        Assert.Equal(1.0, n.Length, 9);
    }

    [Fact]
    public void Normalize_Zero_IsNoOp()
    {
        var n = Vec3.Zero.Normalized(out var len);
        Assert.Equal(0.0, len);
        Assert.Equal(Vec3.Zero, n);
    }

    [Fact]
    public void ProjectOntoNormal_GivesScaledNormal()
    {
        var a = new Vec3(2, 3, 0);
        var n = new Vec3(1, 0, 0);
        Assert.Equal(new Vec3(2, 0, 0), a.ProjectOntoNormal(n));
    }
}

public class Mat4Tests
{
    [Fact]
    public void Multiply_ByIdentity_IsUnchanged()
    {
        var t = Mat4.Translate(new Vec3(2, 3, 4));
        var r = t * Mat4.Identity;
        for (int i = 0; i < 16; i++)
            Assert.Equal(t.M[i], r.M[i], 6);
    }

    [Fact]
    public void Translate_StoresInColumnMajorTranslationColumn()
    {
        var t = Mat4.Translate(new Vec3(5, 6, 7));
        Assert.Equal(5f, t.M[12]);
        Assert.Equal(6f, t.M[13]);
        Assert.Equal(7f, t.M[14]);
    }

    [Fact]
    public void Perspective_PutsNegativeWInClipForwardPoint()
    {
        // A right-handed perspective should map a point in front of the camera
        // (negative Z in view space) to positive clip-space w (= -z).
        var p = Mat4.Perspective(System.Math.PI / 2, 1.0, 0.1, 100.0);
        // w row is the third column's .w via M[11] = -1
        Assert.Equal(-1f, p.M[11]);
    }

    [Theory]
    [InlineData(8.0, 6.0, 0.5, 40.0)]
    [InlineData(21.0, 15.75, 10.0, 34.0)]
    [InlineData(0.002, 1000000.0, 0.001, 100000.0)]
    public void Orthographic_MapsItsBoxCornersToVulkanNdc(double width, double height, double near, double far)
    {
        var o = Mat4.Orthographic(width, height, near, far);
        (double X, double Y, double Z, double W) Clip(double x, double y, double z) => (
            o.M[0] * x + o.M[4] * y + o.M[8] * z + o.M[12],
            o.M[1] * x + o.M[5] * y + o.M[9] * z + o.M[13],
            o.M[2] * x + o.M[6] * y + o.M[10] * z + o.M[14],
            o.M[3] * x + o.M[7] * y + o.M[11] * z + o.M[15]);
        foreach (int sx in new[] { -1, 1 })
            foreach (int sy in new[] { -1, 1 })
                foreach (var (z, depth) in new[] { (-near, 0.0), (-far, 1.0) })
                {
                    var c = Clip(sx * width / 2, sy * height / 2, z);
                    Assert.Equal(1.0, c.W);                  // parallel rays: no perspective divide
                    Assert.Equal(sx, c.X, 5);
                    Assert.Equal(-sy, c.Y, 5);               // Vulkan clip Y points down
                    Assert.Equal(depth, c.Z, 4);
                }
        // Only the documented entries are set.
        int[] used = [0, 5, 10, 14, 15];
        for (int i = 0; i < 16; i++) if (!used.Contains(i)) Assert.Equal(0f, o.M[i]);
    }

    [Fact]
    public void Orthographic_RejectsEmptyOrNonfiniteVolumes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Mat4.Orthographic(0, 1, 0.1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mat4.Orthographic(1, -1, 0.1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mat4.Orthographic(1, 1, 5, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Mat4.Orthographic(double.NaN, 1, 0.1, 10));
    }
}

public class ModelTests
{
    [Fact]
    public void Surface_RecalcNormal_FacesUpForCcwFloor()
    {
        var level = new Sed.Core.Model.Level();
        var sec = level.NewSector();
        var s = sec.NewSurface();
        foreach (var p in new[] { new Vec3(0, 0, 0), new Vec3(1, 0, 0), new Vec3(1, 1, 0) })
            s.Corners.Add(new Sed.Core.Model.Surface.Corner { Vertex = sec.AddVertex(p) });

        s.RecalcNormal();
        Assert.Equal(0.0, s.Normal.X, 9);
        Assert.Equal(0.0, s.Normal.Y, 9);
        Assert.Equal(1.0, s.Normal.Z, 9);
    }

    [Fact]
    public void Level_AssignsUniqueIds()
    {
        var level = new Sed.Core.Model.Level();
        var a = level.NewSector();
        var b = level.NewSector();
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(0, a.Num);
        Assert.Equal(1, b.Num);
    }
}

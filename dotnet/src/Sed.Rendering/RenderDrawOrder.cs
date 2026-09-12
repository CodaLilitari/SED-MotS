using Sed.Core.Math;

namespace Sed.Rendering;

/// <summary>Camera-relative deferred ordering, independent of material batching.</summary>
public static class RenderDrawOrder
{
    public static double MinimumDepth(Mat4 viewProjection, Vec3 a, Vec3 b, Vec3 c)
    {
        var m = viewProjection.M;
        if (m is null || m.Length != 16) throw new ArgumentException("Expected a 4x4 view-projection matrix.");
        // With our perspective projection clip W is forward camera-space depth.
        // Orthographic/test matrices have constant W; their clip Z supplies the order.
        bool perspective = m[3] != 0 || m[7] != 0 || m[11] != 0;
        int row = perspective ? 3 : 2;
        double Depth(Vec3 p) => m[row] * p.X + m[row + 4] * p.Y + m[row + 8] * p.Z + m[row + 12];
        double depth = System.Math.Min(Depth(a), System.Math.Min(Depth(b), Depth(c)));
        if (!double.IsFinite(depth)) throw new ArgumentException("Deferred geometry has nonfinite depth.");
        return depth;
    }
}

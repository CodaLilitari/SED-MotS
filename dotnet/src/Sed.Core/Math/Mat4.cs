namespace Sed.Core.Math;

/// <summary>
/// Single-precision 4x4 matrix in column-major storage (GLSL/Vulkan layout):
/// element (row r, col c) lives at M[c*4 + r]. Projection uses Vulkan clip-space
/// conventions (Y points down, depth in [0,1]).
/// </summary>
public struct Mat4
{
    public float[] M; // length 16, column-major

    private Mat4(float[] m) => M = m;

    public static Mat4 Identity => new(new float[]
    {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    });

    /// <summary>Returns a*b (apply b first, then a — same as GLSL a*b).</summary>
    public static Mat4 operator *(Mat4 a, Mat4 b)
    {
        var r = new float[16];
        for (int col = 0; col < 4; col++)
        for (int row = 0; row < 4; row++)
        {
            float sum = 0;
            for (int k = 0; k < 4; k++)
                sum += a.M[k * 4 + row] * b.M[col * 4 + k];
            r[col * 4 + row] = sum;
        }
        return new Mat4(r);
    }

    /// <summary>Right-handed look-at view matrix.</summary>
    public static Mat4 LookAt(Vec3 eye, Vec3 target, Vec3 up)
    {
        var f = (target - eye).Normalized();      // forward
        var s = f.Cross(up).Normalized();          // right
        var u = s.Cross(f);                         // true up

        return new Mat4(new float[]
        {
            (float)s.X, (float)u.X, (float)-f.X, 0,
            (float)s.Y, (float)u.Y, (float)-f.Y, 0,
            (float)s.Z, (float)u.Z, (float)-f.Z, 0,
            (float)-s.Dot(eye), (float)-u.Dot(eye), (float)f.Dot(eye), 1,
        });
    }

    /// <summary>
    /// Perspective projection for Vulkan: right-handed input, depth range [0,1],
    /// with the Y axis flipped to match Vulkan's clip space.
    /// </summary>
    public static Mat4 Perspective(double fovYRadians, double aspect, double near, double far)
    {
        double f = 1.0 / System.Math.Tan(fovYRadians / 2.0);
        var m = new float[16];
        m[0] = (float)(f / aspect);
        m[5] = (float)-f;                          // Y flip for Vulkan
        m[10] = (float)(far / (near - far));
        m[11] = -1f;
        m[14] = (float)((far * near) / (near - far));
        return new Mat4(m);
    }

    /// <summary>
    /// Orthographic projection for Vulkan, centred on the view axis: <paramref name="width"/> by
    /// <paramref name="height"/> world units map to NDC [-1, 1] with Y flipped, and view-space
    /// z = -near maps to depth 0, z = -far to depth 1. Clip W stays 1 (parallel rays).
    /// </summary>
    public static Mat4 Orthographic(double width, double height, double near, double far)
    {
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height))
            throw new ArgumentOutOfRangeException(nameof(width), "Orthographic extents must be finite and positive.");
        if (!double.IsFinite(near) || !double.IsFinite(far) || near == far)
            throw new ArgumentOutOfRangeException(nameof(near), "Orthographic clipping planes must be finite and distinct.");
        var m = new float[16];
        m[0] = (float)(2.0 / width);
        m[5] = (float)(-2.0 / height);             // Y flip for Vulkan
        m[10] = (float)(1.0 / (near - far));
        m[14] = (float)(near / (near - far));
        m[15] = 1f;
        return new Mat4(m);
    }

    public static Mat4 Translate(Vec3 t)
    {
        var m = Identity;
        m.M[12] = (float)t.X;
        m.M[13] = (float)t.Y;
        m.M[14] = (float)t.Z;
        return m;
    }

    public static Mat4 RotateX(double r)
    {
        float c = (float)System.Math.Cos(r), s = (float)System.Math.Sin(r);
        var m = Identity;
        m.M[5] = c; m.M[6] = s; m.M[9] = -s; m.M[10] = c;
        return m;
    }

    public static Mat4 RotateY(double r)
    {
        float c = (float)System.Math.Cos(r), s = (float)System.Math.Sin(r);
        var m = Identity;
        m.M[0] = c; m.M[2] = -s; m.M[8] = s; m.M[10] = c;
        return m;
    }

    public static Mat4 RotateZ(double r)
    {
        float c = (float)System.Math.Cos(r), s = (float)System.Math.Sin(r);
        var m = Identity;
        m.M[0] = c; m.M[1] = s; m.M[4] = -s; m.M[5] = c;
        return m;
    }

    /// <summary>
    /// Sith-engine object orientation from (pitch, yaw, roll) in degrees:
    /// yaw about +Z (up), pitch about +X, roll about +Y, applied yaw·pitch·roll.
    /// </summary>
    public static Mat4 FromPyr(double pitchDeg, double yawDeg, double rollDeg)
    {
        const double d2r = System.Math.PI / 180.0;
        return RotateZ(yawDeg * d2r) * RotateX(pitchDeg * d2r) * RotateY(rollDeg * d2r);
    }

    /// <summary>Transforms a position (w=1) by this matrix.</summary>
    public Vec3 TransformPoint(Vec3 p)
    {
        double x = p.X, y = p.Y, z = p.Z;
        return new Vec3(
            M[0] * x + M[4] * y + M[8] * z + M[12],
            M[1] * x + M[5] * y + M[9] * z + M[13],
            M[2] * x + M[6] * y + M[10] * z + M[14]);
    }
}

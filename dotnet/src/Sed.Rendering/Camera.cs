using Sed.Core.Math;

namespace Sed.Rendering;

/// <summary>How a <see cref="Camera"/> projects the scene.</summary>
public enum CameraProjection
{
    /// <summary>Rays converge on the eye (vertical field of view).</summary>
    Perspective,
    /// <summary>Parallel rays along the view direction (an orthographic plan or elevation).</summary>
    Orthographic,
}

/// <summary>
/// Where the renderer's facing and sky tests take the viewer to be. A perspective eye is a
/// point; an orthographic eye is a direction, because every ray is parallel to the view.
/// </summary>
/// <param name="Position">The camera position (the ray origin for a perspective view).</param>
/// <param name="TowardCamera">For an orthographic view, the unit direction from the scene toward the camera.</param>
/// <param name="Orthographic">True for parallel rays.</param>
public readonly record struct RenderEye(Vec3 Position, Vec3 TowardCamera, bool Orthographic)
{
    /// <summary>A point eye: facing is measured toward <paramref name="position"/>.</summary>
    public static RenderEye Perspective(Vec3 position) => new(position, Vec3.Zero, false);

    /// <summary>A parallel eye looking along <paramref name="forward"/>: facing is measured against the view direction.</summary>
    public static RenderEye Parallel(Vec3 position, Vec3 forward) => new(position, (forward * -1).Normalized(), true);
}

/// <summary>A free-look 3D camera for the level preview (replaces PREVIEW.PAS view state).</summary>
public sealed class Camera
{
    public Vec3 Position { get; set; } = Vec3.Zero;
    public double Yaw { get; set; }
    public double Pitch { get; set; }
    public double FieldOfViewDegrees { get; set; } = 90.0;
    public double NearPlane { get; set; } = 0.01;
    public double FarPlane { get; set; } = 1000.0;
    /// <summary>Perspective (default) or orthographic projection.</summary>
    public CameraProjection Projection { get; set; } = CameraProjection.Perspective;
    /// <summary>World units visible vertically in an orthographic view; the width is this times the aspect ratio.</summary>
    public double OrthographicHeight { get; set; } = 10.0;

    public Vec3 Forward
    {
        get
        {
            double cy = System.Math.Cos(Yaw), sy = System.Math.Sin(Yaw);
            double cp = System.Math.Cos(Pitch), sp = System.Math.Sin(Pitch);
            return new Vec3(sy * cp, cy * cp, sp).Normalized();
        }
    }

    /// <summary>World up. The Sith engine is Z-up (sector floors face +Z).</summary>
    public static Vec3 Up => new(0, 0, 1);

    /// <summary>
    /// True when the camera looks straight up or down (pitch ±90°), where forward × up has no
    /// reliable direction; its screen axes then come from the yaw alone.
    /// </summary>
    public bool IsVertical => System.Math.Abs(System.Math.Cos(Pitch)) < 1e-9;

    /// <summary>
    /// Camera-right unit vector (screen +X). For a vertical view it is the yaw's right
    /// (yaw 0 gives +X), which equals the limit of forward × up as the pitch approaches ±90°.
    /// </summary>
    public Vec3 Right => IsVertical
        ? new Vec3(System.Math.Cos(Yaw), -System.Math.Sin(Yaw), 0)
        : Forward.Cross(Up).Normalized();

    /// <summary>Camera-up unit vector (screen +Y), tilts with pitch. Looking down at yaw 0 it is +Y (north).</summary>
    public Vec3 CameraUp => Right.Cross(Forward).Normalized();

    /// <summary>The renderer's view of this camera's eye: a point for perspective, a direction for orthographic.</summary>
    public RenderEye Eye => Projection == CameraProjection.Orthographic
        ? RenderEye.Parallel(Position, Forward)
        : RenderEye.Perspective(Position);

    /// <summary>Creates a camera positioned at <paramref name="eye"/> aimed at <paramref name="target"/>.</summary>
    public static Camera LookingAt(Vec3 eye, Vec3 target, double fovDegrees = 60.0)
    {
        var dir = (target - eye).Normalized();
        return new Camera
        {
            Position = eye,
            Pitch = System.Math.Asin(System.Math.Clamp(dir.Z, -1, 1)),
            Yaw = System.Math.Atan2(dir.X, dir.Y),
            FieldOfViewDegrees = fovDegrees,
        };
    }

    /// <summary>Builds the combined projection * view matrix for the given aspect ratio.</summary>
    public Mat4 ViewProjection(double aspect)
    {
        // A vertical view has no usable world-up reference, so it orients by the camera's own up.
        var view = Mat4.LookAt(Position, Position + Forward, IsVertical ? CameraUp : Up);
        var proj = Projection == CameraProjection.Orthographic
            ? Mat4.Orthographic(OrthographicHeight * aspect, OrthographicHeight, NearPlane, FarPlane)
            : Mat4.Perspective(FieldOfViewDegrees * System.Math.PI / 180.0, aspect, NearPlane, FarPlane);
        return proj * view;
    }
}

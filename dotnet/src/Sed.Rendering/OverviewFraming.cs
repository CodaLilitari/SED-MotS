using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Rendering;

/// <summary>
/// One computed overview camera: where it stands, what it looks at, and its projection.
/// <see cref="FieldOfViewDegrees"/> is set for perspective views and
/// <see cref="OrthographicHeight"/> for orthographic ones; the other is zero.
/// </summary>
public readonly record struct OverviewCamera(Vec3 Position, Vec3 Target, string Projection,
    double FieldOfViewDegrees, double OrthographicHeight, double Near, double Far);

/// <summary>
/// Wren's level-preview cameras, generalised. Wren (a Grok bot, one of Andrei's AI
/// collaborators) framed each level from its bounds with an orthographic overhead plan and a
/// three-quarter perspective from the south-east; recovered 2026-10-31. This is the shared
/// "Wren framing" rule (SPEC §2.1) that SED's <c>sed-agent overview</c> and the Blender
/// previewer in tools/blender both reproduce to 1e-9, framed on the drawn world surfaces only
/// (geo ≠ 0, three or more corners, a material; sky included; things and models excluded).
/// The optional <c>contain</c> fit keeps Wren's direction but backs off until the whole box fits.
/// </summary>
public static class OverviewFraming
{
    public const string Overhead = "overhead";
    public const string ThreeQuarter = "threequarter";
    public const string Wren = "wren";
    public const string Contain = "contain";
    public const double DefaultMargin = 0.025;
    public const double MaximumMargin = 0.25;

    /// <summary>
    /// The axis-aligned box of every corner of every drawn world surface (the predicate the lit
    /// scene uses, <see cref="SceneAssembler.IsDrawnWorldSurface"/>), or null when there is none.
    /// </summary>
    public static Box? WorldSurfaceBounds(Level level)
    {
        ArgumentNullException.ThrowIfNull(level);
        var box = Box.Empty;
        bool any = false;
        foreach (var sector in level.Sectors)
            foreach (var surface in sector.Surfaces)
            {
                if (!SceneAssembler.IsDrawnWorldSurface(surface)) continue;
                foreach (var corner in surface.Corners)
                {
                    var p = corner.Vertex.Position;
                    if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z))
                        throw new FormatException("Level geometry contains a nonfinite vertex; nothing can be framed.");
                    box.Encapsulate(p);
                    any = true;
                }
            }
        return any ? box : null;
    }

    /// <summary>
    /// Frames <paramref name="bounds"/> for a <paramref name="width"/> × <paramref name="height"/> image.
    /// <paramref name="view"/> is <c>overhead</c> (orthographic, straight down, north up) or
    /// <c>threequarter</c> (perspective from the south-east, above); <paramref name="framing"/>
    /// is <c>wren</c> (Wren's fixed distance) or <c>contain</c> (back off until the box fits).
    /// </summary>
    /// <exception cref="ArgumentException">An unknown view or framing, a nonpositive image size or a margin outside 0..0.25.</exception>
    /// <exception cref="InvalidOperationException">The box is empty, or flat in both X and Y.</exception>
    public static OverviewCamera Compute(Box bounds, string view, string framing, int width, int height, double margin)
    {
        if (view is not (Overhead or ThreeQuarter)) throw new ArgumentException("view must be one of: overhead, threequarter.");
        if (framing is not (Wren or Contain)) throw new ArgumentException("framing must be one of: wren, contain.");
        if (width < 1 || height < 1) throw new ArgumentException("Overview image dimensions must be positive.");
        if (!double.IsFinite(margin) || margin < 0 || margin > MaximumMargin)
            throw new ArgumentException("margin must be finite and from 0 through 0.25.");
        var min = bounds.Min; var max = bounds.Max;
        if (!Finite(min) || !Finite(max) || min.X > max.X || min.Y > max.Y || min.Z > max.Z)
            throw new InvalidOperationException("Level has no drawn world surfaces to frame.");
        double aspect = (double)width / height;
        var center = (min + max) * 0.5;
        var size = max - min;
        double diagonal = size.Length, radius = diagonal / 2;
        if (System.Math.Max(size.X, size.Y) < 1e-6)
            throw new InvalidOperationException("Level bounds are degenerate in X and Y; nothing to frame.");

        if (view == Overhead)
        {
            // Straight down, north (+Y) up and east (+X) right. Both fits are identical: an
            // orthographic plan already shows the whole footprint at its fitted extent.
            double h = System.Math.Max(size.X, size.Y);
            var position = new Vec3(center.X, center.Y, max.Z + h);
            double orthographicHeight = System.Math.Max(size.Y, size.X / aspect) * (1 + 2 * margin);
            double near = System.Math.Clamp(0.5 * h, 0.001, 1000);
            double far = (position.Z - min.Z) + near;
            return new OverviewCamera(position, center, "orthographic", 0, orthographicHeight, near, far);
        }

        // Blender lens 35 mm on a 36 mm sensor with AUTO fit: the wider image side spans 36 mm.
        double tanV = 18.0 / 35.0 / System.Math.Max(aspect, 1.0);
        double fovY = 2 * System.Math.Atan(tanV);
        double tanH = aspect * tanV;
        var direction = new Vec3(0.7, -0.7, 0.6).Normalized(); // target → eye: south-east, above
        Vec3 eye;
        double distance;
        if (framing == Wren)
        {
            // Wren's own placement: three-quarters of the box diagonal along (0.7, -0.7, 0.6).
            double d = 0.75 * diagonal;
            eye = center + new Vec3(0.7 * d, -0.7 * d, 0.6 * d);
            distance = (eye - center).Length;
        }
        else
        {
            var forward = direction * -1;
            var right = forward.Cross(Camera.Up).Normalized();
            var up = right.Cross(forward);
            double kH = (1 - 2 * margin) * tanH, kV = (1 - 2 * margin) * tanV;
            double d = 0;
            foreach (double x in new[] { min.X, max.X })
                foreach (double y in new[] { min.Y, max.Y })
                    foreach (double z in new[] { min.Z, max.Z })
                    {
                        var q = new Vec3(x, y, z) - center;
                        double depth = forward.Dot(q);
                        d = System.Math.Max(d, System.Math.Max(System.Math.Abs(right.Dot(q)) / kH - depth,
                            System.Math.Abs(up.Dot(q)) / kV - depth));
                    }
            eye = center + direction * d;
            distance = d;
        }
        double nearPlane = distance > radius
            ? System.Math.Clamp(0.5 * (distance - radius), 0.001, 1000)
            : System.Math.Max(0.001, 0.001 * radius);
        double farPlane = distance + 1.01 * radius;
        return new OverviewCamera(eye, center, "perspective", fovY * 180.0 / System.Math.PI, 0, nearPlane, farPlane);
    }

    private static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
}

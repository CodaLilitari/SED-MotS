using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Aligns a surface's UVs to a neighbour it shares an edge with, so the texture
/// runs continuously across the seam (the original's align-from-adjoin).
///
/// The shared edge is the common axis. The reference surface's UV mapping is
/// decomposed into a gradient *along* that edge and one *perpendicular* to it;
/// the target is then given the same two gradients, anchored at the shared edge.
/// The result matches exactly along the seam and continues at the same texture
/// scale onto the target — which is what "continuous" means for two faces meeting
/// at an angle, since they are not coplanar and cannot share one flat projection.
/// </summary>
public sealed class AlignTextureToNeighbourCommand : IEditCommand
{
    private readonly Surface _target;
    private readonly Surface _reference;
    private readonly List<TexVertex> _old = new();

    public AlignTextureToNeighbourCommand(Surface target, Surface reference)
    {
        _target = target;
        _reference = reference;
    }

    public string Name => "Align texture to neighbour";

    /// <summary>Null when the pair can be aligned, else why not.</summary>
    public static string? Validate(Surface target, Surface reference)
        => Calculate(target, reference, out _);

    // Compute and validate the complete result before touching UVs or cached
    // normals. GUI previews and agent plans must reject the same geometry.
    private static string? Calculate(Surface target, Surface reference, out TexVertex[] result)
    {
        result = [];
        if (ReferenceEquals(target, reference)) return "Pick two different surfaces.";
        if (target.Corners.Count is < 3 or > 64 || reference.Corners.Count is < 3 or > 64)
            return "Both surfaces need between three and 64 vertices.";
        foreach (var surface in new[] { target, reference })
        {
            if (surface.Corners.Any(c => c is null || c.Vertex is null || !Finite(c.Vertex.Position)
                || !double.IsFinite(c.Uv.U) || !double.IsFinite(c.Uv.V)))
                return "Surfaces need finite vertices and texture coordinates.";
            if (surface.Corners.Where((c, i) => (c.Vertex.Position
                - surface.Corners[(i + 1) % surface.Corners.Count].Vertex.Position).LengthSquared < 1e-18).Any())
                return "Surfaces must not contain zero-length edges.";
            var normal = surface.CalculateNormal();
            if (!Finite(normal) || normal.LengthSquared < .99)
                return "Both surfaces need nonzero area.";
            if (surface.Corners.Any(c => System.Math.Abs(normal.Dot(c.Vertex.Position - surface.Corners[0].Vertex.Position)) > 1e-6))
                return "Texture alignment requires planar surfaces.";
        }
        if (FindSharedEdge(target, reference, out var edge) is { } edgeProblem)
            return edgeProblem;
        var (a, b, referenceA, referenceB) = edge;
        var along = b - a;
        double length = along.Length;
        if (!double.IsFinite(length) || length < 1e-9) return "The shared edge is degenerate.";
        along *= 1.0 / length;

        // UV gradient along the shared edge, in UV units per world unit.
        // Keep the matched corners: a fresh tolerance lookup can select the
        // same corner for both endpoints of a short, but valid, shared edge.
        var uvA = referenceA.Uv;
        var uvB = referenceB.Uv;
        var gradAlong = new TexVertex((uvB.U - uvA.U) / length, (uvB.V - uvA.V) / length);

        // Recover the reference mapping in its own basis. A near-only shared
        // edge can differ slightly in length/direction from the target's edge.
        var refAnchor = referenceA.Vertex.Position;
        var refAlong = referenceB.Vertex.Position - refAnchor;
        double refLength = refAlong.Length;
        if (!double.IsFinite(refLength) || refLength < 1e-9) return "The shared reference edge is degenerate.";
        refAlong *= 1.0 / refLength;
        var refGradAlong = new TexVertex((uvB.U - uvA.U) / refLength, (uvB.V - uvA.V) / refLength);
        var perpRef = InwardPerpendicular(reference, refAlong, refAnchor);
        if (!TryPerpendicularGradient(reference, refAnchor, refAlong, perpRef, uvA, refGradAlong, out var gradPerp))
            return "The reference surface has no usable texture gradient across the shared edge.";
        if (!HasAffineMapping(reference, refAnchor, refAlong, perpRef, uvA, refGradAlong, gradPerp))
            return "Texture alignment requires an affine reference UV mapping; its corner gradients disagree.";

        var perpTarget = InwardPerpendicular(target, along, a);
        if (!Finite(perpTarget) || perpTarget.LengthSquared < .99)
            return "The target surface is degenerate across the shared edge.";
        if (System.Math.Abs(reference.CalculateNormal().Dot(target.CalculateNormal())) > 1 - 1e-10
            && perpRef.Dot(perpTarget) > 0)
            return "Coplanar neighbours must lie on opposite sides of the shared edge.";

        var computed = new List<TexVertex>();
        foreach (var corner in target.Corners)
        {
            var d = corner.Vertex.Position - a;
            double u = d.Dot(along);
            double v = d.Dot(perpTarget);

            var uv = new TexVertex(
                uvA.U + gradAlong.U * u - gradPerp.U * v,
                uvA.V + gradAlong.V * u - gradPerp.V * v);
            if (!double.IsFinite(uv.U) || !double.IsFinite(uv.V)
                || System.Math.Abs(uv.U) > 1_000_000 || System.Math.Abs(uv.V) > 1_000_000)
                return "Aligned texture coordinates must be finite and within +/-1000000 texels.";
            computed.Add(uv);
        }
        result = computed.ToArray();
        return null;
    }

    public void Apply()
    {
        if (Calculate(_target, _reference, out var result) is { } error)
            throw new InvalidOperationException(error);
        _old.Clear();
        foreach (var c in _target.Corners) _old.Add(c.Uv);
        for (int i = 0; i < result.Length; i++) _target.Corners[i].Uv = result[i];
    }

    private static bool Finite(Vec3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    public void Revert()
    {
        for (int i = 0; i < _target.Corners.Count && i < _old.Count; i++)
            _target.Corners[i].Uv = _old[i];
    }

    /// <summary>
    /// Matches one edge and preserves its endpoint correspondence. Exact
    /// coordinates win over tolerance matches; ambiguous approximate direction
    /// and multiple edges are refused instead of depending on corner order.
    /// Vertex identity cannot be used: adjoined sectors keep their own objects.
    /// </summary>
    private static string? FindSharedEdge(Surface x, Surface y, out SharedEdge edge)
    {
        const double tol = 1e-6;
        edge = default;
        var exact = new List<SharedEdge>();
        var approximate = new List<SharedEdge>();
        bool ambiguousOrientation = false;

        for (int i = 0; i < x.Corners.Count; i++)
        {
            var xa = x.Corners[i].Vertex.Position;
            var xb = x.Corners[(i + 1) % x.Corners.Count].Vertex.Position;

            for (int j = 0; j < y.Corners.Count; j++)
            {
                var ca = y.Corners[j];
                var cb = y.Corners[(j + 1) % y.Corners.Count];
                var ya = ca.Vertex.Position;
                var yb = cb.Vertex.Position;
                if (xa == ya && xb == yb) exact.Add(new SharedEdge(xa, xb, ca, cb));
                else if (xa == yb && xb == ya) exact.Add(new SharedEdge(xa, xb, cb, ca));
                else
                {
                    bool forward = Near(xa, ya, tol) && Near(xb, yb, tol);
                    bool reverse = Near(xa, yb, tol) && Near(xb, ya, tol);
                    if (forward && reverse) ambiguousOrientation = true;
                    else if (forward) approximate.Add(new SharedEdge(xa, xb, ca, cb));
                    else if (reverse) approximate.Add(new SharedEdge(xa, xb, cb, ca));
                }
            }
        }
        if (exact.Count == 0 && ambiguousOrientation)
            return "The near-matched shared edge has ambiguous endpoint orientation.";
        var candidates = exact.Count > 0 ? exact : approximate;
        if (candidates.Count == 0) return "The surfaces do not share an edge.";
        if (candidates.Count != 1) return "Texture alignment requires one unambiguous shared edge; multiple shared edges were found.";
        edge = candidates[0];
        return null;
    }

    private readonly record struct SharedEdge(Vec3 A, Vec3 B, Surface.Corner ReferenceA, Surface.Corner ReferenceB);

    private static bool Near(Vec3 p, Vec3 q, double tol) => (p - q).LengthSquared < tol * tol;

    /// <summary>
    /// A unit vector in the surface's plane, perpendicular to the edge and
    /// pointing into the surface (so its corners have positive coordinates).
    /// </summary>
    private static Vec3 InwardPerpendicular(Surface surf, Vec3 along, Vec3 anchor)
    {
        var perp = surf.CalculateNormal().Cross(along);
        if (perp.LengthSquared < 1e-18) return perp;
        perp = perp.Normalized();

        double sum = 0;
        foreach (var c in surf.Corners) sum += (c.Vertex.Position - anchor).Dot(perp);
        return sum < 0 ? perp * -1.0 : perp;
    }

    /// <summary>
    /// Recovers the reference's UV change per world unit away from the edge, by
    /// looking at the corner furthest from it (the most numerically stable one).
    /// </summary>
    private static bool TryPerpendicularGradient(Surface reference, Vec3 anchor, Vec3 along,
        Vec3 perp, TexVertex uvAnchor, TexVertex gradAlong, out TexVertex gradPerp)
    {
        gradPerp = new TexVertex(0, 0);
        if (perp.LengthSquared < 1e-18) return false;

        Surface.Corner? best = null;
        double bestDistance = 0;

        foreach (var c in reference.Corners)
        {
            double distance = (c.Vertex.Position - anchor).Dot(perp);
            if (distance > bestDistance) { bestDistance = distance; best = c; }
        }

        if (best is null || bestDistance < 1e-9) return false;

        var d = best.Vertex.Position - anchor;
        double u = d.Dot(along);

        // Subtract the along-edge contribution; what remains is the perpendicular one.
        gradPerp = new TexVertex(
            (best.Uv.U - uvAnchor.U - gradAlong.U * u) / bestDistance,
            (best.Uv.V - uvAnchor.V - gradAlong.V * u) / bestDistance);
        return true;
    }

    private static bool HasAffineMapping(Surface reference, Vec3 anchor, Vec3 along,
        Vec3 perp, TexVertex uvAnchor, TexVertex gradAlong, TexVertex gradPerp)
    {
        static bool Agrees(double expected, double actual) => double.IsFinite(expected)
            && System.Math.Abs(expected - actual) <= 1e-6 + 1e-10 * System.Math.Max(System.Math.Abs(expected), System.Math.Abs(actual));
        foreach (var corner in reference.Corners)
        {
            var d = corner.Vertex.Position - anchor;
            double u = d.Dot(along), v = d.Dot(perp);
            if (!Agrees(uvAnchor.U + gradAlong.U * u + gradPerp.U * v, corner.Uv.U)
                || !Agrees(uvAnchor.V + gradAlong.V * u + gradPerp.V * v, corner.Uv.V))
                return false;
        }
        return true;
    }
}

using Sed.Core.Math;

namespace Sed.Core.Editing;

/// <summary>Deterministic, bounded placement positions; no geometry or entities are changed.</summary>
public static class PlacementArrayLayout
{
    public const int MaxCount = 256;
    public const double MaxCoordinate = 1_000_000;

    public static IReadOnlyList<Vec3> Linear(Vec3 origin, Vec3 step, int count)
    {
        RequireVector(origin, nameof(origin)); RequireVector(step, nameof(step)); RequireCount(count);
        return CheckedPositions(Enumerable.Range(0, count).Select(i => origin + step * i).ToArray());
    }

    /// <summary>U changes fastest; V selects each successive row.</summary>
    public static IReadOnlyList<Vec3> Grid(Vec3 origin, Vec3 stepU, Vec3 stepV, int countU, int countV)
    {
        RequireVector(origin, nameof(origin)); RequireVector(stepU, nameof(stepU)); RequireVector(stepV, nameof(stepV));
        RequireCount(countU); RequireCount(countV);
        RequireCount(checked(countU * countV));
        var points = new Vec3[countU * countV];
        for (int v = 0; v < countV; v++)
            for (int u = 0; u < countU; u++) points[v * countU + u] = origin + stepU * u + stepV * v;
        return CheckedPositions(points);
    }

    /// <summary>
    /// Uses the least-aligned cardinal axis (X then Y then Z on ties), projected
    /// perpendicular to axis, as U; V = axis cross U. Angles advance positively
    /// from U toward V. Both full and partial sweeps exclude their endpoint:
    /// angle[i] = startDegrees + sweepDegrees * i / count. Orientation is separate.
    /// </summary>
    public static IReadOnlyList<Vec3> Radial(Vec3 origin, Vec3 axis, double radius, int count,
        double startDegrees, double sweepDegrees)
    {
        RequireVector(origin, nameof(origin)); RequireVector(axis, nameof(axis)); RequireCount(count);
        if (!double.IsFinite(radius) || radius is <= 0 or > MaxCoordinate)
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be finite and in (0, 1000000].");
        if (!double.IsFinite(startDegrees) || System.Math.Abs(startDegrees) > MaxCoordinate)
            throw new ArgumentOutOfRangeException(nameof(startDegrees), "Start angle must be finite and within +/-1000000 degrees.");
        if (!double.IsFinite(sweepDegrees) || sweepDegrees == 0 || System.Math.Abs(sweepDegrees) > 360)
            throw new ArgumentOutOfRangeException(nameof(sweepDegrees), "Sweep must be finite, nonzero and within +/-360 degrees.");
        // Scale first: very small valid axes must not underflow during normalization.
        double largest = System.Math.Max(System.Math.Abs(axis.X), System.Math.Max(System.Math.Abs(axis.Y), System.Math.Abs(axis.Z)));
        if (largest == 0) throw new ArgumentException("Radial axis must be nonzero.", nameof(axis));
        var normal = (axis / largest).Normalized();
        var helper = System.Math.Abs(normal.X) <= System.Math.Abs(normal.Y) && System.Math.Abs(normal.X) <= System.Math.Abs(normal.Z)
            ? new Vec3(1, 0, 0) : System.Math.Abs(normal.Y) <= System.Math.Abs(normal.Z) ? new Vec3(0, 1, 0) : new Vec3(0, 0, 1);
        var u = (helper - normal * helper.Dot(normal)).Normalized();
        var v = normal.Cross(u);
        var points = new Vec3[count];
        for (int i = 0; i < count; i++)
        {
            double angle = (startDegrees + sweepDegrees * i / count) * (System.Math.PI / 180);
            points[i] = origin + u * (radius * System.Math.Cos(angle)) + v * (radius * System.Math.Sin(angle));
        }
        return CheckedPositions(points);
    }

    internal static IReadOnlyList<Vec3> CheckedPositions(IReadOnlyList<Vec3> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        RequireCount(positions.Count);
        var copy = positions.ToArray();
        var unique = new HashSet<Vec3>();
        foreach (var point in copy)
        {
            RequireVector(point, nameof(positions));
            if (!unique.Add(point)) throw new ArgumentException("Array positions must be distinct; a zero or overlapping step creates duplicate placements.", nameof(positions));
        }
        return Array.AsReadOnly(copy);
    }

    internal static void RequireVector(Vec3 vector, string parameter)
    {
        if (new[] { vector.X, vector.Y, vector.Z }.Any(value => !double.IsFinite(value) || System.Math.Abs(value) > MaxCoordinate))
            throw new ArgumentOutOfRangeException(parameter, "Coordinates and angles must be finite and within +/-1000000.");
    }

    private static void RequireCount(int count)
    {
        if (count is < 1 or > MaxCount)
            throw new ArgumentOutOfRangeException(nameof(count), $"An array must contain 1 through {MaxCount} outputs.");
    }
}

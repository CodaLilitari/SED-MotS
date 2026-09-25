using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Query;

public enum ThingContainmentStatus { Unique, Ambiguous, NoContainingSector, UnsupportedGeometry, InvalidThing }
public enum ThingSectorAssignmentStatus { Contained, NotContained, Unassigned, OutsideLevel, Unknown }

/// <summary>A snapshot of one containing sector address, including a tolerance-boundary match.</summary>
public sealed record ThingContainmentCandidate(int SectorIndex, bool OnBoundary);

public sealed record ThingContainmentResult(int ThingIndex, Vec3? Position, double Tolerance,
    ThingContainmentStatus Status, ThingSectorAssignmentStatus Assignment, int? AssignedSectorIndex,
    IReadOnlyList<ThingContainmentCandidate> Candidates, string? Detail)
{
    /// <summary>Null when there is no live assignment or its containment could not be verified.</summary>
    public bool? AssignedSectorMatches => Assignment switch
    {
        ThingSectorAssignmentStatus.Contained => true,
        ThingSectorAssignmentStatus.NotContained => false,
        _ => null,
    };

    /// <summary>Whether any containing candidate is on a boundary; null when there are no verified candidates.</summary>
    public bool? OnBoundary => Status is ThingContainmentStatus.Unique or ThingContainmentStatus.Ambiguous
        ? Candidates.Any(candidate => candidate.OnBoundary) : null;
}

/// <summary>
/// Read-only point containment, using SpatialSelection's closed convex geometry
/// and all-candidate semantics. It tests the thing's position, not its collision
/// volume. Each call scans structural geometry and validates nearby candidate
/// sectors; repeated calls have no cached acceleration. Unsupported geometry
/// produces an unknown result, never a partial list presented as complete.
/// </summary>
public static class ThingContainment
{
    public static ThingContainmentResult Evaluate(Level level, int thingIndex,
        double tolerance = SpatialSelection.DefaultTolerance)
    {
        ArgumentNullException.ThrowIfNull(level);
        if ((uint)thingIndex >= (uint)level.Things.Count)
            throw new ArgumentOutOfRangeException(nameof(thingIndex));
        if (!double.IsFinite(tolerance) || tolerance is < 1e-9 or > 1e-3)
            throw new ArgumentOutOfRangeException(nameof(tolerance),
                "Distance tolerance must be finite and between 1e-9 and 1e-3 world units.");

        var thing = level.Things[thingIndex];
        int assignedIndex = thing?.Sector is { } assigned ? level.Sectors.IndexOf(assigned) : -1;
        var assignment = thing is null ? ThingSectorAssignmentStatus.Unknown
            : thing.Sector is null ? ThingSectorAssignmentStatus.Unassigned
            : assignedIndex < 0 ? ThingSectorAssignmentStatus.OutsideLevel
            : ThingSectorAssignmentStatus.Unknown;
        var result = new ThingContainmentResult(thingIndex, thing?.Position, tolerance,
            ThingContainmentStatus.InvalidThing, assignment, assignedIndex < 0 ? null : assignedIndex,
            Array.Empty<ThingContainmentCandidate>(), null);
        if (thing is null) return result with { Detail = "The placed thing reference is null." };
        if (!double.IsFinite(thing.Position.X) || !double.IsFinite(thing.Position.Y) || !double.IsFinite(thing.Position.Z))
            return result with { Detail = "The thing position has nonfinite coordinates." };

        IReadOnlyList<SpatialSectorMatch> matches;
        try
        {
            matches = SpatialSelection.SelectSectors(level, thing.Position, tolerance);
        }
        catch (InvalidOperationException error)
        {
            return result with { Status = ThingContainmentStatus.UnsupportedGeometry, Detail = error.Message };
        }

        if (assignedIndex >= 0)
            assignment = matches.Any(match => match.SectorIndex == assignedIndex)
                ? ThingSectorAssignmentStatus.Contained : ThingSectorAssignmentStatus.NotContained;
        return result with
        {
            Status = matches.Count switch
            {
                0 => ThingContainmentStatus.NoContainingSector,
                1 => ThingContainmentStatus.Unique,
                _ => ThingContainmentStatus.Ambiguous,
            },
            Assignment = assignment,
            Candidates = Array.AsReadOnly(matches.Select(match =>
                new ThingContainmentCandidate(match.SectorIndex, match.OnBoundary)).ToArray()),
        };
    }
}

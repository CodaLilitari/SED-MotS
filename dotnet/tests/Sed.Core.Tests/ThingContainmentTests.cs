using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Query;
using Sed.Core.Validation;

namespace Sed.Core.Tests;

public sealed class ThingContainmentTests
{
    [Fact]
    public void InteriorUsesLiveGeometryAndListAddressWithoutMutation()
    {
        var level = new Level(); var sector = AddBox(level, Vec3.Zero);
        sector.Num = 500;
        sector.Center = new(50, 50, 50);
        sector.BoundBox = new(new(49, 49, 49), new(51, 51, 51));
        var thing = level.NewThing(); thing.Num = 900; thing.Sector = sector;
        var vertices = sector.Vertices.Select(vertex => vertex.Position).ToArray();
        var normals = sector.Surfaces.Select(surface => surface.Normal).ToArray();

        var result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(0, result.ThingIndex);
        Assert.Equal(ThingContainmentStatus.Unique, result.Status);
        Assert.Equal(ThingSectorAssignmentStatus.Contained, result.Assignment);
        Assert.Equal(0, result.AssignedSectorIndex);
        Assert.True(result.AssignedSectorMatches);
        Assert.False(result.OnBoundary);
        Assert.Equal(new ThingContainmentCandidate(0, false), Assert.Single(result.Candidates));
        Assert.Null(result.Detail);
        Assert.Empty(ContainmentIssues(level));

        Assert.Equal(vertices, sector.Vertices.Select(vertex => vertex.Position));
        Assert.Equal(normals, sector.Surfaces.Select(surface => surface.Normal));
        Assert.Equal(new(50, 50, 50), sector.Center);
        Assert.Equal(new(49, 49, 49), sector.BoundBox.Value.Min);
        Assert.Equal(new(51, 51, 51), sector.BoundBox.Value.Max);
        Assert.Same(sector, thing.Sector);
        Assert.Equal(Vec3.Zero, thing.Position);
        Assert.Equal(500, sector.Num);
        Assert.Equal(900, thing.Num);
    }

    [Fact]
    public void MovedThingHasDefiniteMismatchButNoAutomaticReassignment()
    {
        var level = new Level(); var first = AddBox(level, Vec3.Zero);
        AddBox(level, new(5, 0, 0));
        var thing = level.NewThing(); thing.Sector = first; thing.Position = new(5, 0, 0);
        var result = ThingContainment.Evaluate(level, 0);

        Assert.Equal(ThingContainmentStatus.Unique, result.Status);
        Assert.Equal(ThingSectorAssignmentStatus.NotContained, result.Assignment);
        Assert.False(result.AssignedSectorMatches);
        Assert.Equal(1, Assert.Single(result.Candidates).SectorIndex);
        Assert.DoesNotContain(ConsistencyChecker.Check(level), issue => issue.Message.Contains("not contained"));
        var issue = Assert.Single(ContainmentIssues(level));
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Equal(0, issue.ItemIndex);
        Assert.Equal(0, issue.SectorIndex);
        Assert.Contains("candidates: 1", issue.Message);
        Assert.Same(first, thing.Sector);
        Assert.Equal(new(5, 0, 0), thing.Position);
    }

    [Fact]
    public void OutsideAllSectorsIsDifferentFromUnsupportedGeometry()
    {
        var level = new Level(); var sector = AddBox(level, Vec3.Zero);
        var thing = level.NewThing(); thing.Sector = sector; thing.Position = new(5, 5, 5);
        var result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(ThingContainmentStatus.NoContainingSector, result.Status);
        Assert.Equal(ThingSectorAssignmentStatus.NotContained, result.Assignment);
        Assert.Empty(result.Candidates);
        Assert.False(result.AssignedSectorMatches);
        Assert.Null(result.OnBoundary);
        Assert.Contains("candidates: none", Assert.Single(ContainmentIssues(level)).Message);
    }

    [Fact]
    public void IntentionalOverlapReturnsAllCandidatesWithoutWarningForAValidAssignment()
    {
        var level = new Level(); AddBox(level, Vec3.Zero); var assigned = AddBox(level, Vec3.Zero);
        var thing = level.NewThing(); thing.Sector = assigned;
        var result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(ThingContainmentStatus.Ambiguous, result.Status);
        Assert.Equal(new[] { 0, 1 }, result.Candidates.Select(candidate => candidate.SectorIndex));
        Assert.True(result.AssignedSectorMatches);
        Assert.False(result.OnBoundary);
        Assert.Empty(ContainmentIssues(level));
        Assert.Same(assigned, thing.Sector);
    }

    [Fact]
    public void SharedBoundaryRemainsAmbiguousAndReportsEveryBoundaryCandidate()
    {
        var level = new Level(); var assigned = AddBox(level, Vec3.Zero); AddBox(level, new(2, 0, 0));
        var thing = level.NewThing(); thing.Sector = assigned; thing.Position = new(1, 0, 0);
        var result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(ThingContainmentStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.True(candidate.OnBoundary));
        Assert.True(result.OnBoundary);
        Assert.True(result.AssignedSectorMatches);
        Assert.Empty(ContainmentIssues(level));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAndForeignAssignmentsKeepVerifiedCandidates(bool foreign)
    {
        var level = new Level(); AddBox(level, Vec3.Zero); var thing = level.NewThing();
        if (foreign) thing.Sector = AddBox(new Level(), Vec3.Zero);
        var result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(ThingContainmentStatus.Unique, result.Status);
        Assert.Equal(foreign ? ThingSectorAssignmentStatus.OutsideLevel : ThingSectorAssignmentStatus.Unassigned,
            result.Assignment);
        Assert.Single(result.Candidates);
        Assert.Null(result.AssignedSectorIndex);
        Assert.Null(result.AssignedSectorMatches);
        Assert.Single(ConsistencyChecker.Check(level, new() { CheckThingContainment = true }),
            issue => issue.Type == ItemType.Thing);
    }

    [Fact]
    public void InvalidUnrelatedSectorProducesConservativeUnknownInsteadOfPartialMatches()
    {
        var level = new Level(); var near = AddBox(level, Vec3.Zero); var far = AddBox(level, new(100, 0, 0));
        far.Surfaces[0].Corners[0].Vertex = near.Vertices[0];
        var thing = level.NewThing(); thing.Sector = near;
        var result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(ThingContainmentStatus.UnsupportedGeometry, result.Status);
        Assert.Equal(ThingSectorAssignmentStatus.Unknown, result.Assignment);
        Assert.Empty(result.Candidates);
        Assert.Null(result.AssignedSectorMatches);
        Assert.Null(result.OnBoundary);
        Assert.Contains("sector 1", result.Detail);
        var issue = Assert.Single(ContainmentIssues(level));
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("not verified", issue.Message);
        Assert.DoesNotContain("not contained", issue.Message);
    }

    [Fact]
    public void OpenNearbyVolumeIsUnknownButSafelyBoundedDistantOpenVolumeIsCulled()
    {
        var level = new Level(); var near = AddBox(level, Vec3.Zero); var far = AddBox(level, new(100, 0, 0));
        far.Surfaces.RemoveAt(0);
        var thing = level.NewThing(); thing.Sector = near;
        Assert.Equal(ThingContainmentStatus.Unique, ThingContainment.Evaluate(level, 0).Status);
        thing.Position = new(100, 0, 0);
        var result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(ThingContainmentStatus.UnsupportedGeometry, result.Status);
        Assert.Contains("not closed", result.Detail);
    }

    [Fact]
    public void ExplicitToleranceChangesOnlyGeometricMembershipAndBoundaryClassification()
    {
        var level = new Level(); var sector = AddBox(level, Vec3.Zero);
        var thing = level.NewThing(); thing.Sector = sector; thing.Position = new(1.000005, 0, 0);
        Assert.Equal(ThingContainmentStatus.NoContainingSector, ThingContainment.Evaluate(level, 0).Status);
        var result = ThingContainment.Evaluate(level, 0, 1e-5);
        Assert.Equal(1e-5, result.Tolerance);
        Assert.Equal(ThingContainmentStatus.Unique, result.Status);
        Assert.True(result.OnBoundary);
        Assert.True(result.AssignedSectorMatches);
        Assert.Equal(new(1.000005, 0, 0), thing.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1e-2)]
    [InlineData(double.NaN)]
    public void InvalidToleranceIsAnArgumentErrorRatherThanUnknownGeometry(double tolerance)
    {
        var level = new Level(); level.NewThing();
        Assert.Throws<ArgumentOutOfRangeException>(() => ThingContainment.Evaluate(level, 0, tolerance));
    }

    [Fact]
    public void InvalidThingDataIsDistinctFromGeometryFailure()
    {
        var level = new Level(); AddBox(level, Vec3.Zero);
        var thing = level.NewThing(); thing.Position = new(double.NaN, 0, 0);
        var result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(ThingContainmentStatus.InvalidThing, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Contains("nonfinite", result.Detail);
        level.Things[0] = null!;
        result = ThingContainment.Evaluate(level, 0);
        Assert.Equal(ThingContainmentStatus.InvalidThing, result.Status);
        Assert.Null(result.Position);
        Assert.Equal(ThingSectorAssignmentStatus.Unknown, result.Assignment);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void InvalidThingAddressIsRefused(int index)
    {
        var level = new Level(); level.NewThing();
        Assert.Throws<ArgumentOutOfRangeException>(() => ThingContainment.Evaluate(level, index));
    }

    private static Sector AddBox(Level level, Vec3 center)
    {
        var sector = SectorFactory.CreateBox(level, center, 1, "synthetic.mat", 0);
        level.Sectors.Add(sector);
        return sector;
    }

    private static IEnumerable<ConsistencyIssue> ContainmentIssues(Level level) =>
        ConsistencyChecker.Check(level, new() { CheckThingContainment = true })
            .Where(issue => issue.Type == ItemType.Thing &&
                (issue.Message.Contains("containment") || issue.Message.Contains("not contained")));
}

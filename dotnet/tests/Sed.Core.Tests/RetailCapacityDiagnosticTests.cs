using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Validation;

namespace Sed.Core.Tests;

public sealed class RetailCapacityDiagnosticTests
{
    [Theory]
    [InlineData(408, 664, 664)]
    [InlineData(641, 10, 641)]
    [InlineData(0, 641, 641)]
    public void ExplicitRetailWarningUsesSerializedCapacityAndReportsBothCounts(int count, int declared, int serialized)
    {
        var level = CreateLevel(count, declared);
        Assert.Empty(RetailIssues(level));
        var warning = Assert.Single(RetailIssues(level, new() { RetailProfile = true }));
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Contains($"{count} placed things", warning.Message);
        Assert.Contains($"serialized capacity {serialized}", warning.Message);
        Assert.Contains($"declared {declared}", warning.Message);
        Assert.Contains("inferred", warning.Message);
        Assert.Contains("not a complete retail capacity model", warning.Message);
        Assert.Equal(count, level.Things.Count);
        Assert.Equal(declared, level.ThingCapacity);
    }

    [Theory]
    [InlineData(640, 0)]
    [InlineData(408, 640)]
    [InlineData(0, 0)]
    public void AtOrBelowInferredRangeDoesNotWarn(int count, int declared)
        => Assert.Empty(RetailIssues(CreateLevel(count, declared), new() { RetailProfile = true }));

    [Fact]
    public void InfernalMachineDoesNotUseJkRetailPointerWarning()
    {
        var level = CreateLevel(0, 664);
        level.Kind = ProjectType.InfernalMachine;
        Assert.Empty(RetailIssues(level, new() { RetailProfile = true }));
    }

    private static Level CreateLevel(int count, int declared)
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith, ThingCapacity = declared };
        var sector = SectorFactory.CreateBox(level, Vec3.Zero, 1, "synthetic.mat", 0);
        level.Sectors.Add(sector);
        for (int i = 0; i < count; i++) level.NewThing().Sector = sector;
        return level;
    }

    private static IEnumerable<ConsistencyIssue> RetailIssues(Level level, ConsistencyOptions? options = null) =>
        ConsistencyChecker.Check(level, options).Where(issue => issue.Message.Contains("Retail compatibility"));
}

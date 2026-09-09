using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Validation;

namespace Sed.Core.Tests;

public sealed class EngineConsistencyTests
{
    private static Level Box()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.Sectors.Add(SectorFactory.CreateBox(level, Vec3.Zero, 1, "test.mat", 0));
        return level;
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, 24, false)]
    [InlineData(ProjectType.JediKnight, 25, true)]
    [InlineData(ProjectType.MysteriesOfTheSith, 24, false)]
    [InlineData(ProjectType.MysteriesOfTheSith, 25, true)]
    [InlineData(ProjectType.InfernalMachine, 64, false)]
    [InlineData(ProjectType.InfernalMachine, 65, true)]
    public void VertexLimitMatchesEachEngine(ProjectType game, int count, bool exceeds)
    {
        var level = new Level { Kind = game };
        var sector = level.NewSector();
        var surface = sector.NewSurface();
        for (int i = 0; i < count; i++)
            surface.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(new Vec3(System.Math.Cos(i * System.Math.Tau / count), System.Math.Sin(i * System.Math.Tau / count), 0)) });
        var issues = ConsistencyChecker.Check(level).Where(i => i.Message.Contains("max vertex count")).ToArray();
        if (!exceeds) Assert.Empty(issues);
        else Assert.Equal(game == ProjectType.InfernalMachine ? IssueSeverity.Warning : IssueSeverity.Error, Assert.Single(issues).Severity);
    }

    [Fact]
    public void CheckingDoesNotChangeCachedNormals()
    {
        var level = Box();
        var surface = level.Sectors[0].Surfaces[0];
        Vec3 cached = surface.Normal;
        surface.Corners[0].Vertex.Position += new Vec3(3, 2, 1);
        ConsistencyChecker.Check(level);
        Assert.Equal(cached, surface.Normal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1e-10)]
    public void CollapsedAdjacentEdgeIsRejectedEvenWhenPolygonRetainsArea(double offset)
    {
        var level = Box();
        var surface = level.Sectors[0].Surfaces[0];
        surface.Corners[1].Vertex.Position = surface.Corners[0].Vertex.Position + new Vec3(offset, 0, 0);
        Assert.Contains(ConsistencyChecker.Check(level), i => i.Severity == IssueSeverity.Error && i.Message.Contains("zero-length edge"));
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, IssueSeverity.Error)]
    [InlineData(ProjectType.MysteriesOfTheSith, IssueSeverity.Error)]
    [InlineData(ProjectType.InfernalMachine, IssueSeverity.Warning)]
    public void ZeroAreaSurfaceHasGameAppropriateSeverity(ProjectType game, IssueSeverity severity)
    {
        var level = new Level { Kind = game };
        var sector = level.NewSector();
        var surface = sector.NewSurface();
        for (int i = 0; i < 3; i++) surface.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(new Vec3(i, 0, 0)) });
        Assert.Equal(severity, Assert.Single(ConsistencyChecker.Check(level), i => i.Message.Contains("normal is invalid")).Severity);
    }

    [Fact]
    public void MissingAndNullCornersReportErrorsWithoutDereferencing()
    {
        var level = Box();
        var surface = level.Sectors[0].Surfaces[0];
        surface.Corners[0].Vertex = new Vertex(Vec3.Zero);
        Assert.Contains(ConsistencyChecker.Check(level), i => i.Message.Contains("missing vertex") && i.Severity == IssueSeverity.Error);
        surface.Corners[0] = null!;
        Assert.Contains(ConsistencyChecker.Check(level), i => i.Message.Contains("missing vertex"));
    }

    [Fact]
    public void RemovedAdjoinAndThingSectorsAreRejected()
    {
        var level = Box();
        var removed = new Sector(level);
        level.Sectors[0].Surfaces[0].Adjoin = new Surface(removed);
        level.NewThing().Sector = removed;
        var issues = ConsistencyChecker.Check(level);
        Assert.Contains(issues, i => i.Message.Contains("Adjoin references") && i.Severity == IssueSeverity.Error);
        Assert.Contains(issues, i => i.Type == ItemType.Thing && i.Message.Contains("outside this level"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteVertexAndThingDataAreErrors(double value)
    {
        var level = Box();
        level.Sectors[0].Vertices[0].Position = new Vec3(value, 0, 0);
        level.NewThing().Yaw = value;
        var issues = ConsistencyChecker.Check(level);
        Assert.Contains(issues, i => i.Type == ItemType.Sector && i.Severity == IssueSeverity.Error && i.Message.Contains("non-finite"));
        Assert.Contains(issues, i => i.Type == ItemType.Thing && i.Severity == IssueSeverity.Error && i.Message.Contains("non-finite"));
    }

    [Theory]
    [InlineData("-1", CogIndexStatus.Null)]
    [InlineData("-2147483648", CogIndexStatus.Null)]
    [InlineData("0", CogIndexStatus.Level)]
    [InlineData("+1", CogIndexStatus.Level)]
    [InlineData("2", CogIndexStatus.UnplacedUnchecked)]
    [InlineData("32768", CogIndexStatus.StaticUnchecked)]
    [InlineData("32769", CogIndexStatus.StaticUnchecked)]
    [InlineData("2147483647", CogIndexStatus.StaticUnchecked)]
    [InlineData("0x8000", CogIndexStatus.Invalid)]
    [InlineData("2foo", CogIndexStatus.Invalid)]
    [InlineData("2147483648", CogIndexStatus.Invalid)]
    [InlineData("", CogIndexStatus.Invalid)]
    [InlineData("+", CogIndexStatus.Invalid)]
    [InlineData("none", CogIndexStatus.Invalid)]
    public void CogIndexNullStaticAndRuntimeRules(string value, CogIndexStatus expected)
        => Assert.Equal(expected, CogReferenceRules.CheckCogIndex(value, 2));

    [Fact]
    public void TypedCogChecksBoundsWithoutMistakingRuntimeIdsForErrors()
    {
        var level = Box();
        var cog = new Cog();
        cog.Values.AddRange(["0", "1", "6", "32768", "99", "-1", "0x10"]);
        level.Cogs.Add(cog);
        var options = new ConsistencyOptions { ResolveCogParameters = _ => new[]
        {
            new CogParameterDefinition("sectorOk", CogReferenceKind.Sector), new("sectorBad", CogReferenceKind.Sector),
            new("surfaceBad", CogReferenceKind.Surface), new("staticCog", CogReferenceKind.Cog),
            new("runtimeCog", CogReferenceKind.Cog), new("nullThing", CogReferenceKind.Thing), new("badCog", CogReferenceKind.Cog),
        } };
        var issues = ConsistencyChecker.Check(level, options).Where(i => i.Type == ItemType.Cog).ToArray();
        Assert.Equal(3, issues.Count(i => i.Severity == IssueSeverity.Error));
        Assert.Equal(2, issues.Count(i => i.Severity == IssueSeverity.Warning));
        Assert.DoesNotContain(issues, i => i.ParameterIndex is 0 or 5);
        Assert.All(issues, i => Assert.Equal(0, i.ItemIndex));
    }

    [Fact]
    public void RawCogValuesAreNotGuessedToBeReferences()
    {
        var level = Box();
        var cog = new Cog();
        cog.Values.Add("999999");
        level.Cogs.Add(cog);
        Assert.DoesNotContain(ConsistencyChecker.Check(level), i => i.Type == ItemType.Cog);
        var unresolved = ConsistencyChecker.Check(level, new ConsistencyOptions { ResolveCogParameters = _ => null });
        Assert.Contains(unresolved, i => i.Type == ItemType.Cog && i.Severity == IssueSeverity.Warning && i.Message.Contains("unavailable"));
    }

    [Fact]
    public void RetailBaselineIsOnlyAnExplicitWarningAbove640()
    {
        var level = Box();
        for (int i = 0; i < 640; i++) level.NewThing().Sector = level.Sectors[0];
        var options = new ConsistencyOptions { RetailProfile = true };
        Assert.DoesNotContain(ConsistencyChecker.Check(level, options), i => i.Message.Contains("Retail compatibility"));
        level.NewThing().Sector = level.Sectors[0];
        Assert.DoesNotContain(ConsistencyChecker.Check(level), i => i.Message.Contains("Retail compatibility"));
        Assert.Equal(IssueSeverity.Warning, Assert.Single(ConsistencyChecker.Check(level, options), i => i.Message.Contains("Retail compatibility")).Severity);
    }
}

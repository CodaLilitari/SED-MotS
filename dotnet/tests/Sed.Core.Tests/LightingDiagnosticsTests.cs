using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Tests;

public sealed class LightingDiagnosticsTests
{
    [Fact]
    public void FacingRangeAndEligibleCountsMatchActualIsolatedBake()
    {
        var (level, sector, surface, light) = Scene();
        light.Position = new(-1, -1, 1); light.Range = 1.5; light.Flags = LightFlags.NoBlock;
        var diagnostic = Assert.Single(LightCalculator.Diagnose(level).Lights);
        Assert.Equal(LightDiagnosticStatus.Contributing, diagnostic.Status);
        var samples = diagnostic.Samples!;
        Assert.Equal(4, samples.Candidate);
        Assert.Equal(0, samples.FacingRejected);
        Assert.Equal(3, samples.RangeRejected);
        Assert.Equal(1, samples.Eligible);
        Assert.Equal(1, samples.ShadowBypassedNoBlock);
        Assert.Equal(0, samples.ShadowTested);
        Assert.Equal(1, samples.NonzeroMono);
        Assert.Equal(1, samples.NonzeroColor);
        AssertPartition(samples);

        LightCalculator.Calculate(level, new[] { sector }, new() { UpdateSectorAmbients = false });
        Assert.Equal(samples.NonzeroMono, surface.Corners.LongCount(corner => corner.MonoIntensity != 0));
        Assert.Equal(samples.NonzeroColor, surface.Corners.LongCount(corner => corner.Intensity.R != 0));
        Assert.Equal(1f / 9, surface.Corners[0].Intensity.R, 6);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(0, 4)]
    [InlineData(0.000999, 4)]
    [InlineData(0.001, 0)]
    public void FacingThresholdMatchesBakePredicate(double height, int rejected)
    {
        var (level, _, _, light) = Scene();
        light.Position = new(0, 0, height);
        var samples = Assert.Single(LightCalculator.Diagnose(level, options: new() { CastShadows = false }).Lights).Samples!;
        Assert.Equal(rejected, samples.FacingRejected);
        Assert.Equal(4 - rejected, samples.Eligible);
        AssertPartition(samples);
    }

    [Fact]
    public void RangeBoundaryIsExcluded()
    {
        var (level, _, _, light) = Scene();
        light.Position = new(-1, -1, 1); light.Range = 1;
        var row = Assert.Single(LightCalculator.Diagnose(level).Lights);
        Assert.Equal(LightDiagnosticStatus.NoEligibleSamples, row.Status);
        Assert.Equal(4, row.Samples!.RangeRejected);
        Assert.Equal(0, row.Samples.ShadowTested);
    }

    [Theory]
    [InlineData(0, 1, LightDiagnosticStatus.DisabledRange)]
    [InlineData(-1, 1, LightDiagnosticStatus.DisabledRange)]
    [InlineData(10, 0, LightDiagnosticStatus.ZeroIntensity)]
    public void InactiveLightsExplainSkippedSampleTests(double range, double intensity, LightDiagnosticStatus status)
    {
        var (level, _, _, light) = Scene(); light.Range = range; light.Intensity = intensity;
        var row = Assert.Single(LightCalculator.Diagnose(level).Lights);
        Assert.Equal(status, row.Status);
        Assert.Equal(4, row.Samples!.Candidate);
        Assert.Equal(0, row.Samples.FacingRejected + row.Samples.RangeRejected + row.Samples.Eligible);
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, 0, 2, 0, 0)]
    [InlineData(ProjectType.MysteriesOfTheSith, 0, 2, 0, 4)]
    [InlineData(ProjectType.MysteriesOfTheSith, 1, 0, 4, 0)]
    [InlineData(ProjectType.InfernalMachine, 0, 2, 0, 0)]
    [InlineData(ProjectType.JediKnight, 1, 0, 4, 4)]
    public void ConsumedChannelsFollowTheSelectedGame(ProjectType game, double mono, double rgb, int monoCount, int colorCount)
    {
        var (level, _, surface, light) = Scene(); level.Kind = game;
        light.Intensity = mono; light.RgbIntensity = rgb; light.LegacyRgbRange = 0.01;
        var row = Assert.Single(LightCalculator.Diagnose(level, options: new() { CastShadows = false }).Lights);
        Assert.Equal(monoCount, row.Samples!.NonzeroMono);
        Assert.Equal(colorCount, row.Samples.NonzeroColor);
        LightCalculator.Calculate(level, options: new() { CastShadows = false, UpdateSectorAmbients = false });
        Assert.Equal(monoCount, surface.Corners.Count(corner => corner.MonoIntensity != 0));
        Assert.Equal(colorCount, surface.Corners.Count(corner => corner.Intensity.R != 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EligibleSamplesCanStillHaveZeroOutput(bool underflow)
    {
        var (level, _, _, light) = Scene(); level.Kind = ProjectType.MysteriesOfTheSith;
        light.Intensity = underflow ? double.Epsilon : 0;
        light.RgbIntensity = underflow ? 0 : 2;
        light.Color = ColorF.Black;
        var row = Assert.Single(LightCalculator.Diagnose(level).Lights);
        Assert.Equal(LightDiagnosticStatus.ZeroContribution, row.Status);
        Assert.Equal(4, row.Samples!.Eligible);
        Assert.Equal(0, row.Samples.NonzeroMono + row.Samples.NonzeroColor);
    }

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(1, 0, 4)]
    [InlineData(2, 0, 4)]
    public void OcclusionAndExplicitShadowBypassesUseBakePredicates(int mode, int shadowed, int eligible)
    {
        var (level, sector, _, light) = Scene();
        AddQuad(level.NewSector(), 0.5, 2);
        if (mode == 2) light.Flags = LightFlags.NoBlock;
        var options = new LightingOptions { CastShadows = mode != 1, UpdateSectorAmbients = false };
        var samples = Assert.Single(LightCalculator.Diagnose(level, new[] { sector }, options).Lights).Samples!;
        Assert.Equal(shadowed, samples.Occluded);
        Assert.Equal(eligible, samples.Eligible);
        Assert.Equal(mode == 0 ? 4 : 0, samples.ShadowTested);
        Assert.Equal(mode == 1 ? 4 : 0, samples.ShadowBypassedDisabled);
        Assert.Equal(mode == 2 ? 4 : 0, samples.ShadowBypassedNoBlock);
        AssertPartition(samples);
        Assert.Equal(samples.Occluded, LightCalculator.Calculate(level, new[] { sector }, options).Shadowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PortalBlockLightFlagChangesOcclusion(bool block)
    {
        var (level, sector, _, _) = Scene();
        var portal = AddQuad(level.NewSector(), 0.5, 2);
        var mirror = AddQuad(level.NewSector(), 0.5, 2);
        portal.Adjoin = mirror; mirror.Adjoin = portal;
        if (block) { portal.AdjoinFlags = AdjoinFlags.BlockLight; mirror.AdjoinFlags = AdjoinFlags.BlockLight; }
        var samples = Assert.Single(LightCalculator.Diagnose(level, new[] { sector }).Lights).Samples!;
        Assert.Equal(block ? 4 : 0, samples.Occluded);
        Assert.Equal(block ? 0 : 4, samples.Eligible);
        Assert.Equal(samples.Occluded, LightCalculator.Calculate(level, new[] { sector }).Shadowed);
    }

    [Fact]
    public void SameSectorShortcutAndAllCachesRemainUnchangedByDiagnostics()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var box = SectorFactory.CreateBox(level, Vec3.Zero, 1, "synthetic.mat", 0); level.Sectors.Add(box);
        AddQuad(level.NewSector(), 0.5, 2);
        var light = level.NewLight(); light.Range = 10; light.Intensity = 0.5; light.RgbIntensity = 2;
        box.Ambient = new(0.2f, 0.3f, 0.4f);
        foreach (var corner in box.Surfaces.SelectMany(surface => surface.Corners))
        { corner.Intensity = new(0.1f, 0.4f, 0.7f); corner.MonoIntensity = null; }
        var surfaces = level.Sectors.SelectMany(sector => sector.Surfaces).ToArray();
        var beforeNormals = surfaces.Select(surface => surface.Normal).ToArray();
        var beforeCorners = surfaces.SelectMany(surface => surface.Corners).Select(corner => (corner.Intensity, corner.MonoIntensity)).ToArray();
        var beforeAmbient = level.Sectors.Select(sector => sector.Ambient).ToArray();
        var report = LightCalculator.Diagnose(level, new[] { box, box });
        var row = Assert.Single(report.Lights);
        Assert.Equal(0, row.BakerSectorIndex);
        Assert.Equal(24, row.Samples!.Eligible);
        Assert.Equal(24, row.Samples.ShadowBypassedSameSector);
        Assert.Equal(0, row.Samples.ShadowTested);
        Assert.Equal(new[] { 0 }, report.TargetSectorIndices);
        Assert.Equal(beforeNormals, surfaces.Select(surface => surface.Normal));
        Assert.Equal(beforeCorners, surfaces.SelectMany(surface => surface.Corners).Select(corner => (corner.Intensity, corner.MonoIntensity)));
        Assert.Equal(beforeAmbient, level.Sectors.Select(sector => sector.Ambient));
    }

    [Fact]
    public void StaleNonzeroNormalsAreNotUsedOrReplaced()
    {
        var (level, _, surface, light) = Scene(); surface.RecalcNormal();
        Assert.Equal(new(0, 0, 1), surface.Normal);
        foreach (var corner in surface.Corners)
        { var p = corner.Vertex.Position; corner.Vertex.Position = new(0, p.X, p.Y); }
        light.Position = new(2, 0, 0);
        var row = Assert.Single(LightCalculator.Diagnose(level).Lights);
        Assert.Equal(4, row.Samples!.Eligible);
        Assert.Equal(new(0, 0, 1), surface.Normal);
        LightCalculator.Calculate(level);
        Assert.All(surface.Corners, corner => Assert.True(corner.MonoIntensity > 0));
    }

    [Theory]
    [InlineData("position")]
    [InlineData("range")]
    [InlineData("mono")]
    [InlineData("rgb")]
    [InlineData("color")]
    public void InvalidConsumedLightFieldsHaveUnavailableCounts(string field)
    {
        var (level, _, _, light) = Scene(); level.Kind = ProjectType.MysteriesOfTheSith;
        switch (field)
        {
            case "position": light.Position = new(double.NaN, 0, 0); break;
            case "range": light.Range = double.PositiveInfinity; break;
            case "mono": light.Intensity = double.NaN; break;
            case "rgb": light.RgbIntensity = double.NaN; break;
            case "color": light.Color = new(float.NaN, 0, 0); break;
        }
        var row = Assert.Single(LightCalculator.Diagnose(level).Lights);
        Assert.Equal(LightDiagnosticStatus.InvalidLight, row.Status);
        Assert.Null(row.Samples);
    }

    [Fact]
    public void UnusedRgbMetadataDoesNotChangeJkBakeDiagnostics()
    {
        var (level, _, _, light) = Scene();
        light.RgbIntensity = double.NaN; light.LegacyRgbRange = double.NaN; light.Color = new(float.NaN, 0, 0);
        Assert.Equal(LightDiagnosticStatus.Contributing, Assert.Single(LightCalculator.Diagnose(level).Lights).Status);
    }

    [Fact]
    public void FiniteInputCanStillOverflowOutputAndIsReported()
    {
        var (level, _, _, light) = Scene(); light.Intensity = double.MaxValue;
        var row = Assert.Single(LightCalculator.Diagnose(level).Lights);
        Assert.Equal(LightDiagnosticStatus.NonfiniteContribution, row.Status);
        Assert.Equal(4, row.Samples!.NonfiniteContribution);
        Assert.Equal(4, row.Samples.Eligible);
    }

    [Fact]
    public void OverflowInOneChannelDoesNotHideTheOtherFiniteContribution()
    {
        var (level, _, _, light) = Scene(); level.Kind = ProjectType.MysteriesOfTheSith;
        light.Intensity = double.MaxValue; light.RgbIntensity = 1;
        var row = Assert.Single(LightCalculator.Diagnose(level).Lights);
        Assert.Equal(LightDiagnosticStatus.NonfiniteContribution, row.Status);
        Assert.Equal(4, row.Samples!.NonfiniteContribution);
        Assert.Equal(0, row.Samples.NonzeroMono);
        Assert.Equal(4, row.Samples.NonzeroColor);
    }

    [Fact]
    public void ReusableWorldOcclusionUsesNoSameSectorShortcutAndDoesNotMutateNormals()
    {
        var level = new Level();
        var box = SectorFactory.CreateBox(level, Vec3.Zero, 1, "synthetic.mat", 0); level.Sectors.Add(box);
        AddQuad(level.NewSector(), 0.5, 2);
        var surfaces = level.Sectors.SelectMany(sector => sector.Surfaces).ToArray();
        Assert.All(surfaces, surface => Assert.Equal(Vec3.Zero, surface.Normal));
        LightCalculator.ValidateWorldOcclusionGeometry(level);
        var blocked = LightCalculator.CreateWorldOcclusion(level);
        // Both endpoints are inside the box; the interposed world surface still blocks.
        Assert.True(blocked(Vec3.Zero, new(0.5, 0.5, 0.8)));
        Assert.False(blocked(Vec3.Zero, new(0.2, 0.2, 0.3)));
        Assert.True(blocked(Vec3.Zero, new(0.5, 0.5, 0.8)));
        Assert.All(surfaces, surface => Assert.Equal(Vec3.Zero, surface.Normal));
        Assert.Throws<ArgumentException>(() => blocked(new(double.NaN, 0, 0), Vec3.Zero));
        Assert.Throws<ArgumentException>(() => blocked(new(-double.MaxValue, 0, 0), new(double.MaxValue, 0, 0)));
    }

    [Fact]
    public void ReusableWorldOcclusionKeepsPortalAndEndpointPolicies()
    {
        var level = new Level(); var portal = AddQuad(level.NewSector(), 0.5, 2);
        var mirror = AddQuad(level.NewSector(), 0.5, 2);
        portal.Adjoin = mirror; mirror.Adjoin = portal;
        Assert.False(LightCalculator.CreateWorldOcclusion(level)(Vec3.Zero, new(0, 0, 1)));
        portal.AdjoinFlags = AdjoinFlags.BlockLight;
        var blocked = LightCalculator.CreateWorldOcclusion(level);
        Assert.True(blocked(Vec3.Zero, new(0, 0, 1)));
        Assert.False(blocked(Vec3.Zero, new(0, 0, 0.5)));
        Assert.False(blocked(new(0, 0, 0.5), new(0, 0, 1)));
        Assert.False(blocked(Vec3.Zero, Vec3.Zero));
    }

    [Fact]
    public void WorldOcclusionRefusesInvalidGeometryBeforeReturningAPredicate()
    {
        var (level, _, surface, _) = Scene(); surface.Corners[0].Vertex.Position = new(double.NaN, 0, 0);
        var validation = Assert.Throws<InvalidOperationException>(() => LightCalculator.ValidateWorldOcclusionGeometry(level));
        var creation = Assert.Throws<InvalidOperationException>(() => LightCalculator.CreateWorldOcclusion(level));
        Assert.Contains("World occlusion is unavailable", validation.Message);
        Assert.Equal(validation.Message, creation.Message);
    }

    [Fact]
    public void InvalidGeometryIsUnknownRatherThanAZeroEffectClaim()
    {
        var (level, _, surface, _) = Scene();
        surface.Corners[0].Vertex.Position = new(double.NaN, 0, 0);
        var report = LightCalculator.Diagnose(level);
        Assert.NotNull(report.GeometryIssue);
        Assert.Null(report.CandidateSamples);
        var row = Assert.Single(report.Lights);
        Assert.Equal(LightDiagnosticStatus.UnsupportedGeometry, row.Status);
        Assert.Null(row.Samples);
    }

    [Fact]
    public void LightSelectionAndEmptyTargetsAreExplicit()
    {
        var (level, _, _, _) = Scene(); level.NewLight();
        var report = LightCalculator.Diagnose(level, Array.Empty<Sector>(), lightIndices: new[] { 0, 0 });
        Assert.Empty(report.TargetSectorIndices);
        Assert.Equal(0, report.CandidateSamples);
        var row = Assert.Single(report.Lights);
        Assert.Equal(0, row.LightIndex);
        Assert.Equal(LightDiagnosticStatus.NoEligibleSamples, row.Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => LightCalculator.Diagnose(level, lightIndices: new[] { -1 }));
        Assert.Throws<ArgumentException>(() => LightCalculator.Diagnose(level, new[] { new Level().NewSector() }));
    }

    [Theory]
    [InlineData(50000, false)]
    [InlineData(50001, true)]
    public void SampleLightBudgetIsInclusiveAndRefusesExcessBeforeEvaluation(int corners, bool refuse)
    {
        var level = new Level(); var sector = level.NewSector(); var surface = sector.NewSurface();
        var corner = new Surface.Corner { Vertex = sector.AddVertex(Vec3.Zero), Intensity = ColorF.White };
        surface.Corners.AddRange(Enumerable.Repeat(corner, corners));
        for (int i = 0; i < 1000; i++) level.NewLight(); // zero range skips actual sample work
        if (refuse)
        {
            Assert.Contains("sample-light pair budget", Assert.Throws<InvalidOperationException>(() =>
                LightCalculator.Diagnose(level)).Message);
        }
        else
        {
            var report = LightCalculator.Diagnose(level);
            Assert.Equal(LightCalculator.MaxDiagnosticSampleLightPairs, report.CandidateSamples * report.Lights.Count);
            Assert.All(report.Lights, light => Assert.Equal(LightDiagnosticStatus.DisabledRange, light.Status));
        }
        Assert.Equal(Vec3.Zero, surface.Normal);
        Assert.Equal(ColorF.White, corner.Intensity);
    }

    private static void AssertPartition(LightSampleCounts samples) =>
        Assert.Equal(samples.Candidate, samples.FacingRejected + samples.RangeRejected + samples.Occluded + samples.Eligible);

    private static (Level Level, Sector Sector, Surface Surface, Light Light) Scene()
    {
        var level = new Level(); var sector = level.NewSector(); var surface = AddQuad(sector, 0, 1);
        var light = level.NewLight(); light.Position = new(0, 0, 2); light.Range = 10;
        return (level, sector, surface, light);
    }

    private static Surface AddQuad(Sector sector, double height, double half)
    {
        var surface = sector.NewSurface();
        foreach (var point in new[] { new Vec3(-half, -half, height), new Vec3(half, -half, height),
                     new Vec3(half, half, height), new Vec3(-half, half, height) })
            surface.Corners.Add(new() { Vertex = sector.AddVertex(point), Intensity = ColorF.White, MonoIntensity = 0.3f });
        return surface;
    }
}

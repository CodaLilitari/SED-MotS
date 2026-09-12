using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Validation;

namespace Sed.Core.Tests;

public class IndependentSourceLightTests
{
    private static (Level Level, Sector Sector, Surface Floor, Light Light) Scene(ProjectType game = ProjectType.MysteriesOfTheSith)
    {
        var level = new Level { Kind = game };
        var sector = level.NewSector();
        var floor = sector.NewSurface();
        foreach (var position in new[] { Vec3.Zero, new Vec3(2, 0, 0), new Vec3(2, 2, 0), new Vec3(0, 2, 0) })
            floor.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(position), Intensity = ColorF.Black });
        floor.RecalcNormal();
        var light = level.NewLight();
        light.Position = new Vec3(0, 0, 1);
        light.Range = 4;
        light.Color = new ColorF(1, .5f, .25f);
        light.Flags = LightFlags.NoBlock;
        return (level, sector, floor, light);
    }

    private static void Bake(Level level) => LightCalculator.Calculate(level,
        options: new LightingOptions { CastShadows = false, UpdateSectorAmbients = false });

    [Theory]
    [InlineData(.5, 2)]
    [InlineData(0, 2)]
    [InlineData(2, 0)]
    [InlineData(0, 0)]
    public void MotsWhiteAndRgbAccumulateIndependentlyIncludingZero(double white, double rgb)
    {
        var (level, _, floor, light) = Scene();
        light.Intensity = white;
        light.RgbIntensity = rgb;

        Bake(level);

        // The near corner is one unit from a range-four source: (3/4)^2 = 9/16.
        Assert.Equal(white * .5625, floor.Corners[0].MonoIntensity!.Value, 6);
        Assert.Equal(rgb * .5625, floor.Corners[0].Intensity.R, 6);
        Assert.Equal(rgb * .28125, floor.Corners[0].Intensity.G, 6);
        Assert.Equal(rgb * .140625, floor.Corners[0].Intensity.B, 6);
    }

    [Fact]
    public void EditingEitherSourceIntensityLeavesTheOtherBakedChannelAlone()
    {
        var (level, _, floor, light) = Scene();
        light.Intensity = .5;
        light.RgbIntensity = 2;
        Bake(level);
        var original = floor.Corners.Select(c => (c.MonoIntensity, c.Intensity)).ToArray();

        new SetLightIntensityCommand(light, 1.5).Apply();
        Bake(level);
        Assert.Equal(original.Select(c => c.Intensity), floor.Corners.Select(c => c.Intensity));
        Assert.NotEqual(original[0].MonoIntensity, floor.Corners[0].MonoIntensity);
        var changedWhite = floor.Corners.Select(c => c.MonoIntensity).ToArray();

        new SetLightRgbIntensityCommand(light, .25).Apply();
        Bake(level);
        Assert.Equal(changedWhite, floor.Corners.Select(c => c.MonoIntensity));
        Assert.NotEqual(original[0].Intensity, floor.Corners[0].Intensity);
    }

    [Theory]
    [InlineData(ProjectType.JediKnight)]
    [InlineData(ProjectType.MysteriesOfTheSith)]
    [InlineData(ProjectType.InfernalMachine)]
    public void OldSingleIntensityLightsKeepTheirBakedBehavior(ProjectType game)
    {
        var (level, _, floor, light) = Scene(game);
        light.Intensity = .5;
        Assert.Null(light.RgbIntensity);
        Assert.Null(light.LegacyRgbRange);
        Assert.Equal(.5, light.EffectiveRgbIntensity);

        Bake(level);

        Assert.Equal(.28125f, floor.Corners[0].MonoIntensity);
        Assert.Equal(game == ProjectType.JediKnight
            ? new ColorF(.28125f, .28125f, .28125f)
            : new ColorF(.28125f, .140625f, .0703125f), floor.Corners[0].Intensity);
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, .5)]
    [InlineData(ProjectType.JediKnight, 0)]
    [InlineData(ProjectType.InfernalMachine, .5)]
    [InlineData(ProjectType.InfernalMachine, 0)]
    public void OtherGamesDoNotActivateMotsOnlyIntensity(ProjectType game, double intensity)
    {
        var (level, _, floor, light) = Scene(game);
        light.Intensity = intensity;
        Bake(level);
        var before = floor.Corners.Select(c => (c.Intensity, c.MonoIntensity)).ToArray();

        light.RgbIntensity = 100;
        light.LegacyRgbRange = 200;
        Bake(level);

        Assert.Equal(before, floor.Corners.Select(c => (c.Intensity, c.MonoIntensity)).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(.125)]
    [InlineData(100)]
    public void PreservedLegacyRgbRangeDoesNotInventASecondFalloff(double metadata)
    {
        var (level, _, floor, light) = Scene();
        light.RgbIntensity = 2;
        Bake(level);
        var before = floor.Corners.Select(c => (c.Intensity, c.MonoIntensity)).ToArray();

        light.LegacyRgbRange = metadata;
        Bake(level);

        Assert.Equal(before, floor.Corners.Select(c => (c.Intensity, c.MonoIntensity)).ToArray());
        light.Range = .5; // Both channels use the primary radius, so every floor corner is outside it.
        Bake(level);
        Assert.All(floor.Corners, c => { Assert.Equal(ColorF.Black, c.Intensity); Assert.Equal(0f, c.MonoIntensity); });
    }

    [Fact]
    public void IntensityUndoRestoresExplicitZeroAndInheritedStateExactly()
    {
        var light = new Light { Intensity = .5, LegacyRgbRange = 8.25 };
        var history = new EditHistory();
        history.Do(new SetLightRgbIntensityCommand(light, 0));
        Assert.Equal(0d, light.RgbIntensity);
        history.Do(new SetLightIntensityCommand(light, 2));
        Assert.Equal(0, light.EffectiveRgbIntensity);
        history.Do(new SetLightRgbIntensityCommand(light, null));
        Assert.Null(light.RgbIntensity);
        Assert.Equal(2, light.EffectiveRgbIntensity);

        history.Undo();
        Assert.Equal(0d, light.RgbIntensity);
        history.Undo();
        Assert.Equal(.5, light.Intensity);
        history.Undo();
        Assert.Null(light.RgbIntensity);
        Assert.Equal(.5, light.EffectiveRgbIntensity);
        history.Redo(); history.Redo(); history.Redo();
        Assert.Null(light.RgbIntensity);
        Assert.Equal(2, light.EffectiveRgbIntensity);
        Assert.Equal(8.25, light.LegacyRgbRange);
    }

    [Fact]
    public void LegacyRangeMetadataEditingIsReversibleWithoutChangingThePrimaryRange()
    {
        var light = new Light { Range = 4, LegacyRgbRange = null };
        var history = new EditHistory();
        history.Do(new SetLightLegacyRgbRangeCommand(light, 0));
        Assert.Equal(0d, light.LegacyRgbRange);
        history.Do(new SetLightLegacyRgbRangeCommand(light, 17.123456789));
        Assert.Equal(17.123456789, light.LegacyRgbRange);
        history.Do(new SetLightLegacyRgbRangeCommand(light, null));
        Assert.Null(light.LegacyRgbRange);
        history.Undo();
        Assert.Equal(17.123456789, light.LegacyRgbRange);
        history.Undo();
        Assert.Equal(0d, light.LegacyRgbRange);
        history.Undo();
        Assert.Null(light.LegacyRgbRange);
        history.Redo(); history.Redo(); history.Redo();
        Assert.Null(light.LegacyRgbRange);
        Assert.Equal(4, light.Range);
    }

    [Fact]
    public void BakeUndoRestoresAuthoredChannelsAndRedoReplaysCapturedResults()
    {
        var (level, sector, floor, light) = Scene();
        light.Intensity = .25;
        light.RgbIntensity = 1.5;
        light.LegacyRgbRange = 17;
        sector.Ambient = new ColorF(.2f, .3f, .4f);
        foreach (var corner in floor.Corners)
        { corner.Intensity = new ColorF(.1f, .7f, .4f); corner.MonoIntensity = .8f; }
        var before = floor.Corners.Select(c => (c.Intensity, c.MonoIntensity)).ToArray();
        var history = new EditHistory();
        history.Do(new CalculateLightingCommand(level, options: new LightingOptions { CastShadows = false }));
        var baked = floor.Corners.Select(c => (c.Intensity, c.MonoIntensity)).ToArray();
        var bakedAmbient = sector.Ambient;
        Assert.NotEqual(before[0], baked[0]);

        history.Undo();
        Assert.Equal(before, floor.Corners.Select(c => (c.Intensity, c.MonoIntensity)).ToArray());
        Assert.Equal(new ColorF(.2f, .3f, .4f), sector.Ambient);
        // Redo must replay the reviewed bake even if a source changed independently.
        light.Intensity = 0; light.RgbIntensity = 0;
        history.Redo();
        Assert.Equal(baked, floor.Corners.Select(c => (c.Intensity, c.MonoIntensity)).ToArray());
        Assert.Equal(bakedAmbient, sector.Ambient);
        Assert.Equal(17d, light.LegacyRgbRange);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 0.0)]
    [InlineData(2.25, 8.75)]
    public void ClipboardRetainsIndependentValuesAndNullableIntentAcrossUndo(double? rgb, double? range)
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var light = level.NewLight();
        light.Intensity = .5; light.RgbIntensity = rgb; light.LegacyRgbRange = range;
        light.Position = new Vec3(1, 2, 3);
        var selection = new SelectionSet();
        selection.Add(light);
        var fragment = LevelFragment.Capture(selection, level);
        light.RgbIntensity = 99; light.LegacyRgbRange = 100;
        var paste = new PasteFragmentCommand(level, fragment, new Vec3(10, 0, 0));
        var history = new EditHistory();
        history.Do(paste);
        var clone = Assert.Single(paste.PastedLights);
        Assert.NotSame(light, clone);
        Assert.Equal(rgb, clone.RgbIntensity);
        Assert.Equal(range, clone.LegacyRgbRange);
        Assert.Equal(.5, clone.Intensity);
        Assert.Equal(new Vec3(11, 2, 3), clone.Position);
        history.Undo();
        Assert.DoesNotContain(clone, level.Lights);
        history.Redo();
        Assert.Contains(clone, level.Lights);
        Assert.Equal(rgb, clone.RgbIntensity);
        Assert.Equal(range, clone.LegacyRgbRange);
    }

    [Fact]
    public void CreateDeleteAndMoveUndoRetainIndependentSourceValues()
    {
        var level = new Level();
        var light = new Light { Intensity = .5, RgbIntensity = 2.25, LegacyRgbRange = 8.75 };
        var history = new EditHistory();
        history.Do(new CreateLightCommand(level, light));
        history.Do(new MoveLightCommand(light, new Vec3(2, 0, 0)));
        history.Do(new DeleteLightCommand(level, light));
        history.Undo(); history.Undo(); history.Undo();
        Assert.Empty(level.Lights);
        history.Redo(); history.Redo(); history.Redo();
        history.Undo();
        Assert.Same(light, Assert.Single(level.Lights));
        Assert.Equal(.5, light.Intensity);
        Assert.Equal(2.25, light.RgbIntensity);
        Assert.Equal(8.75, light.LegacyRgbRange);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteIndependentValuesAreRejectedOrDiagnosed(double invalid)
    {
        var light = new Light { RgbIntensity = .5 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetLightRgbIntensityCommand(light, invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetLightLegacyRgbRangeCommand(light, invalid));
        Assert.Equal(.5, light.RgbIntensity);
        var level = new Level();
        level.Lights.Add(light);
        light.RgbIntensity = invalid;
        Assert.Contains(ConsistencyChecker.Check(level), i => i.Type == ItemType.Light && i.Severity == IssueSeverity.Error);
        light.RgbIntensity = null;
        light.LegacyRgbRange = invalid;
        Assert.Contains(ConsistencyChecker.Check(level), i => i.Type == ItemType.Light && i.Severity == IssueSeverity.Error);
        light.LegacyRgbRange = 0;
        Assert.DoesNotContain(ConsistencyChecker.Check(level), i => i.Type == ItemType.Light);
    }
}

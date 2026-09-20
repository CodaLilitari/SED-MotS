using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Tests;

/// <summary>Original analytical fixtures; no renderer or production solver supplies expected values.</summary>
public sealed class ArchitectureAuthoringTests
{
    [Fact]
    public void HalfRangeSamplesUseIndependentChannelsAndPreserveMeshSlots()
    {
        var (level, _) = Scene();
        var light = Source(level, .8, .4);
        var points = new IReadOnlyList<Vec3>[] { new[] { new Vec3(2, 0, 0), new Vec3(4, 0, 0) }, new[] { Vec3.Zero } };
        var samples = ArchitecturalLightingAuthoring.PrepareSamples(level, points);
        Assert.Equal(new[] { 2, 1 }, samples.Meshes.Select(mesh => mesh.Vertices.Count));
        Sample(samples.Meshes[0].Vertices[0], .2, .1, .05, .025);
        Sample(samples.Meshes[0].Vertices[1], 0, 0, 0, 0);
        Sample(samples.Meshes[1].Vertices[0], .8, .4, .2, .1);
        Assert.Empty(level.ArchitecturalLighting);
        Assert.Equal(.8, light.Intensity); Assert.Equal(.4, light.RgbIntensity);
    }

    [Fact]
    public void ExplicitZeroRgbAndLegacyRangeDoNotAlterWhiteOrPrimaryFalloff()
    {
        var (level, _) = Scene();
        var light = Source(level, .8, 0);
        light.LegacyRgbRange = 999;
        var explicitZero = BakeOne(level);
        Sample(explicitZero, .2, 0, 0, 0);
        light.RgbIntensity = null;
        Sample(BakeOne(level), .2, .2, .1, .05);
        light.LegacyRgbRange = 0;
        Sample(BakeOne(level), .2, .2, .1, .05);
        Assert.Null(light.RgbIntensity);
    }

    [Fact]
    public void ContributionsAccumulateWithoutClampingStoredSamples()
    {
        var (level, _) = Scene();
        Source(level, 4, 8); Source(level, 2, 4);
        Sample(BakeOne(level), 1.5, 3, 1.5, .75);
    }

    [Fact]
    public void NondirectionalProfileSamplesBothSidesEquallyWithoutTouchingSectorLighting()
    {
        var (level, _) = Scene();
        Source(level, .8, .4);
        var sector = level.NewSector(); sector.Ambient = new ColorF(.75f, .5f, .25f);
        var surface = sector.NewSurface();
        var corner = new Surface.Corner { Vertex = sector.AddVertex(Vec3.Zero), MonoIntensity = .875f, Intensity = new ColorF(.125f, .25f, .5f) };
        surface.Corners.Add(corner);
        var samples = ArchitecturalLightingAuthoring.PrepareSamples(level,
            new IReadOnlyList<Vec3>[] { new[] { new Vec3(2, 0, 0), new Vec3(-2, 0, 0) } });
        Assert.Equal(samples.Meshes[0].Vertices[0], samples.Meshes[0].Vertices[1]);
        Assert.Equal(.875f, corner.MonoIntensity);
        Assert.Equal(new ColorF(.125f, .25f, .5f), corner.Intensity);
        Assert.Equal(new ColorF(.75f, .5f, .25f), sector.Ambient);
    }

    [Fact]
    public void ReplacingSharedLightingOwnsSamplesAndRestoresExactBindingOnUndoRedo()
    {
        var (level, first) = Scene();
        var second = Eligible(level);
        var original = Object(new(.9375, .125, .25, .5));
        level.ArchitecturalLighting.Add(original);
        first.ArchLightingIndex = second.ArchLightingIndex = 0;
        first.ArchLightingSourceModel = "old.3do";
        first.ArchLightingSourceModelHash = new string('f', 64);
        var oldProvenance = new ArchitectureLightingProvenance("bound", "old.3do", new string('e', 64), "", ArchitecturalLightingAuthoring.SamplesDigest(original));
        first.ArchLightingProvenance = oldProvenance;
        var replacement = Object(new(.25, .5, .75, 1));
        var provenance = Provenance(level, first, replacement);
        var command = new ReplaceThingArchitectureCommand(level, first, replacement, "fixture.3do", provenance);
        replacement.Meshes[0].Vertices[0] = new(9, 9, 9, 9);
        var history = new EditHistory(); history.Do(command);
        Assert.Equal(1, first.ArchLightingIndex); Assert.Equal(0, second.ArchLightingIndex);
        Assert.Same(original, level.ArchitecturalLighting[0]);
        Sample(original.Meshes[0].Vertices[0], .9375, .125, .25, .5);
        Sample(level.ArchitecturalLighting[1].Meshes[0].Vertices[0], .25, .5, .75, 1);
        Assert.Same(provenance, first.ArchLightingProvenance);
        Assert.Empty(first.ArchLightingSourceModelHash);
        history.Undo();
        Assert.Single(level.ArchitecturalLighting); Assert.Equal(0, first.ArchLightingIndex);
        Assert.Equal("old.3do", first.ArchLightingSourceModel);
        Assert.Equal(new string('f', 64), first.ArchLightingSourceModelHash);
        Assert.Same(oldProvenance, first.ArchLightingProvenance);
        history.Redo();
        Assert.Equal(1, first.ArchLightingIndex);
        Sample(level.ArchitecturalLighting[1].Meshes[0].Vertices[0], .25, .5, .75, 1);
        Assert.Equal(0, second.ArchLightingIndex);
    }

    [Fact]
    public void UnbindUndoRestoresAuthoringProvenanceAndSamples()
    {
        var (level, thing) = Scene();
        var samples = Object(new(.1, .2, .3, .4));
        var provenance = Provenance(level, thing, samples);
        new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", provenance).Apply();
        var stored = level.ArchitecturalLighting[thing.ArchLightingIndex];
        var history = new EditHistory(); history.Do(new ClearThingArchitectureCommand(thing));
        Assert.Equal("unbound", ArchitecturalLightingAuthoring.Status(level, thing));
        Assert.Null(thing.ArchLightingProvenance);
        Assert.Same(stored, Assert.Single(level.ArchitecturalLighting));
        history.Undo();
        Assert.Same(provenance, thing.ArchLightingProvenance);
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(level, thing));
        Assert.Same(stored, level.ArchitecturalLighting[thing.ArchLightingIndex]);
    }

    [Fact]
    public void PoseAndSourceChangesBecomeStaleAndUndoRestoresCurrentInputs()
    {
        var (level, thing) = Scene();
        var light = Source(level, .8, .4);
        var samples = Object(BakeOne(level));
        new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", Provenance(level, thing, samples)).Apply();
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(level, thing));
        var history = new EditHistory(); history.Do(new SetThingPositionCommand(thing, new Vec3(1, 0, 0)));
        Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(level, thing));
        history.Undo(); Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(level, thing));
        light.RgbIntensity = 0;
        Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(level, thing));
        light.RgbIntensity = .4;
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(level, thing));
        thing.Yaw = 90; Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(level, thing));
    }

    [Fact]
    public void DirectSampleChangesAreOverriddenEvenWithUnchangedInputs()
    {
        var (level, thing) = Scene();
        var samples = Object(new(.1, .2, .3, .4));
        new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", Provenance(level, thing, samples)).Apply();
        var mesh = level.ArchitecturalLighting[thing.ArchLightingIndex].Meshes[0];
        var original = mesh.Vertices[0];
        mesh.Vertices[0] = original with { Mono = .9 };
        Assert.Equal("overridden", ArchitecturalLightingAuthoring.Status(level, thing));
        Assert.Equal(original.Red, mesh.Vertices[0].Red);
        Assert.Equal(original.Green, mesh.Vertices[0].Green);
        Assert.Equal(original.Blue, mesh.Vertices[0].Blue);
        mesh.Vertices[0] = original;
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(level, thing));
    }

    [Fact]
    public void ImportedSamplesHaveUnknownFreshnessWithoutProvenance()
    {
        var (level, thing) = Scene();
        level.ArchitecturalLighting.Add(Object(new(.1, .2, .3, .4))); thing.ArchLightingIndex = 0;
        Assert.Equal("unknown", ArchitecturalLightingAuthoring.Status(level, thing));
        thing.ArchLightingSourceModelHash = new string('a', 64);
        Assert.Equal("unknown", ArchitecturalLightingAuthoring.Status(level, thing));
    }

    [Fact]
    public void PreparationDriftRefusesWithoutChangingArraysBindingOrHistory()
    {
        var (level, thing) = Scene();
        var light = Source(level, .8, .4);
        var samples = Object(BakeOne(level));
        var command = new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", Provenance(level, thing, samples));
        light.Intensity = .9;
        var history = new EditHistory();
        Assert.Throws<InvalidOperationException>(() => history.Do(command));
        Assert.Empty(level.ArchitecturalLighting); Assert.Equal(-1, thing.ArchLightingIndex);
        Assert.Null(thing.ArchLightingProvenance); Assert.False(history.CanUndo);
    }

    [Fact]
    public void RedoReplaysCapturedSamplesAndReportsLaterInputChangesAsStale()
    {
        var (level, thing) = Scene();
        var light = Source(level, .8, .4);
        var samples = Object(BakeOne(level));
        var history = new EditHistory();
        history.Do(new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", Provenance(level, thing, samples)));
        history.Undo(); light.Intensity = 8;
        history.Redo();
        Sample(level.ArchitecturalLighting[0].Meshes[0].Vertices[0], .2, .1, .05, .025);
        Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(level, thing));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UndoRefusesUnexpectedUsersOrTailWithoutPartiallyRemovingSamples(bool appendTail)
    {
        var (level, thing) = Scene();
        var samples = Object(new(.1, .2, .3, .4));
        var history = new EditHistory();
        history.Do(new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", Provenance(level, thing, samples)));
        if (appendTail) level.ArchitecturalLighting.Add(Object(new(.5, .6, .7, .8)));
        else Eligible(level).ArchLightingIndex = 0;
        var before = level.ArchitecturalLighting.ToArray(); var provenance = thing.ArchLightingProvenance;
        Assert.Throws<InvalidOperationException>(() => history.Undo());
        Assert.Equal(before, level.ArchitecturalLighting);
        Assert.Equal(0, thing.ArchLightingIndex); Assert.Same(provenance, thing.ArchLightingProvenance);
        Assert.True(history.CanUndo); Assert.False(history.CanRedo);
    }

    [Fact]
    public void BoundFiniteNegativeLegacySamplesRemainExactAndDoNotBecomeAClaimedBake()
    {
        var (level, thing) = Scene();
        var samples = Object(new(-.25, -.5, .125, .75));
        var provenance = new ArchitectureLightingProvenance("bound", "fixture.3do", new string('a', 64), "", ArchitecturalLightingAuthoring.SamplesDigest(samples));
        new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", provenance).Apply();
        Sample(level.ArchitecturalLighting[0].Meshes[0].Vertices[0], -.25, -.5, .125, .75);
        Assert.Equal("bound", ArchitecturalLightingAuthoring.Status(level, thing));
        samples.Meshes[0].Vertices[0] = new(double.NaN, 0, 0, 0);
        Assert.Throws<InvalidOperationException>(() => ArchitecturalLightingAuthoring.SamplesDigest(samples));
    }

    [Fact]
    public void WorkBudgetRefusesBeforeEnumeratingOrAllocatingTheSampleProduct()
    {
        var (level, _) = Scene();
        var light = new Light { Range = 4, Intensity = 1, Color = ColorF.White };
        level.Lights.AddRange(Enumerable.Repeat(light, 5000));
        var error = Assert.Throws<InvalidOperationException>(() => ArchitecturalLightingAuthoring.PrepareSamples(level,
            new IReadOnlyList<Vec3>[] { new CountOnlyPoints(10001) }));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(level.ArchitecturalLighting);
    }

    private sealed class CountOnlyPoints(int count) : IReadOnlyList<Vec3>
    {
        public int Count => count;
        public Vec3 this[int index] => throw new NotSupportedException("Point access must not occur for the over-budget fixture.");
        public IEnumerator<Vec3> GetEnumerator() => throw new NotSupportedException("Point enumeration must not occur for the over-budget fixture.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static (Level, Thing) Scene()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        return (level, Eligible(level));
    }
    private static Thing Eligible(Level level)
    {
        var thing = level.NewThing(); thing.Values["model3d"] = "fixture.3do"; thing.Values["thingflags"] = "0x8";
        return thing;
    }
    private static Light Source(Level level, double mono, double? rgb)
    {
        var light = new Light { Position = Vec3.Zero, Range = 4, Intensity = mono, RgbIntensity = rgb, Color = new ColorF(1, .5f, .25f) };
        level.Lights.Add(light); return light;
    }
    private static ArchitecturalLightVertex BakeOne(Level level) => ArchitecturalLightingAuthoring.PrepareSamples(level,
        new IReadOnlyList<Vec3>[] { new[] { new Vec3(2, 0, 0) } }).Meshes[0].Vertices[0];
    private static ArchitecturalLightObject Object(ArchitecturalLightVertex value)
    {
        var result = new ArchitecturalLightObject(); var mesh = new ArchitecturalLightMesh();
        mesh.Vertices.Add(value); result.Meshes.Add(mesh); return result;
    }
    private static ArchitectureLightingProvenance Provenance(Level level, Thing thing, ArchitecturalLightObject samples) =>
        new(ArchitecturalLightingAuthoring.BakeProfile, "fixture.3do", new string('a', 64), ArchitecturalLightingAuthoring.InputsDigest(level, thing), ArchitecturalLightingAuthoring.SamplesDigest(samples));
    private static void Sample(ArchitecturalLightVertex sample, double mono, double red, double green, double blue)
    {
        Assert.Equal(mono, sample.Mono, 12); Assert.Equal(red, sample.Red, 12);
        Assert.Equal(green, sample.Green, 12); Assert.Equal(blue, sample.Blue, 12);
    }
}

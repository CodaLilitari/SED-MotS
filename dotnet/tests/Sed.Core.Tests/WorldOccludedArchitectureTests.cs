using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Tests;

/// <summary>Original analytic fixtures for an editor recipe, not a retail bake oracle.</summary>
public sealed class WorldOccludedArchitectureTests
{
    private const string Profile = ArchitecturalLightingAuthoring.WorldOccludedBakeProfile;
    private static readonly ArchitecturalLightVertex HalfRange = new(.2, .1, .05, .025);

    [Fact]
    public void OriginalProfileDigestAndSamplesRemainUnchangedAndUnoccluded()
    {
        var (level, thing) = Scene();
        const string originalDigest = "f1e53a72be38ec7e3977eb36842f23a004af77c43e77f9564169a182825ae6bc";
        Assert.Equal(originalDigest, ArchitecturalLightingAuthoring.InputsDigest(level, thing));
        var original = Samples(level, ArchitecturalLightingAuthoring.BakeProfile);
        Wall(level, 1);
        level.Lights[0].Flags = LightFlags.NoBlock;
        Assert.Equal(originalDigest, ArchitecturalLightingAuthoring.InputsDigest(level, thing));
        Assert.Equal(ArchitecturalLightingAuthoring.SamplesDigest(original),
            ArchitecturalLightingAuthoring.SamplesDigest(Samples(level, ArchitecturalLightingAuthoring.BakeProfile)));
        Assert.Equal(HalfRange, original.Meshes[0].Vertices[0]);
    }

    [Fact]
    public void BlockingWallRemovesBothChannelsWithoutChangingWorldLightingOrNormals()
    {
        var (level, _) = Scene(); var wall = Wall(level, 1);
        wall.Corners[0].MonoIntensity = .75f; wall.Corners[0].Intensity = new(.25f, .5f, 1);
        wall.Sector.Ambient = new(.1f, .2f, .3f);
        var normal = wall.Normal;
        Assert.Equal(default, One(level));
        Assert.Equal(.75f, wall.Corners[0].MonoIntensity);
        Assert.Equal(new ColorF(.25f, .5f, 1), wall.Corners[0].Intensity);
        Assert.Equal(new ColorF(.1f, .2f, .3f), wall.Sector.Ambient);
        Assert.Equal(normal, wall.Normal);
        Assert.Empty(level.ArchitecturalLighting);
    }

    [Fact]
    public void OpenPortalPassesBothChannelsAndEitherBlockingSideStopsThem()
    {
        var (level, _) = Scene(); var wall = Wall(level, 1); var other = Wall(level, 1);
        wall.Adjoin = other; other.Adjoin = wall;
        Assert.Equal(HalfRange, One(level));
        other.AdjoinFlags = AdjoinFlags.BlockLight;
        Assert.Equal(default, One(level));
        other.AdjoinFlags = 0; wall.AdjoinFlags = AdjoinFlags.BlockLight;
        Assert.Equal(default, One(level));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void EndpointAndOutsideSegmentWallsDoNotShadow(double wallX)
    {
        var (level, _) = Scene(); Wall(level, wallX);
        Assert.Equal(HalfRange, One(level));
    }

    [Fact]
    public void NoBlockBypassesWorldShadowWhileMonoAndRgbStayIndependent()
    {
        var (level, _) = Scene(); Wall(level, 1);
        level.Lights[0].Flags = LightFlags.NoBlock;
        Assert.Equal(HalfRange, One(level));
        level.Lights[0].RgbIntensity = 0;
        Assert.Equal(new ArchitecturalLightVertex(.2, 0, 0, 0), One(level));
        level.Lights[0].Intensity = 0; level.Lights[0].RgbIntensity = .4;
        Assert.Equal(new ArchitecturalLightVertex(0, .1, .05, .025), One(level));
    }

    [Fact]
    public void WorldMaterialNameTranslucencyAndRenderModesDoNotCreateAlphaAwareTracing()
    {
        var (level, _) = Scene(); var wall = Wall(level, 1);
        wall.Material = "transparent-named.mat"; wall.FaceFlags = Surface.FfTranslucent;
        wall.Geo = 0; wall.Light = 0; wall.Tex = 0;
        Assert.Equal(default, One(level)); // World polygon blocks; no MAT pixels are read.
    }

    [Theory]
    [InlineData("geometry")]
    [InlineData("light-flags")]
    [InlineData("portal")]
    [InlineData("portal-flags")]
    [InlineData("surface-flags")]
    [InlineData("sector-flags")]
    public void NewProfileDigestAndStatusBindWorldGeometryAndFlags(string change)
    {
        var (level, thing) = Scene(); var wall = Wall(level, 1); var other = Wall(level, 1);
        wall.Adjoin = other; other.Adjoin = wall;
        var samples = Samples(level); var provenance = Provenance(level, thing, samples);
        new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", provenance).Apply();
        Assert.Equal("current-inputs", ArchitecturalLightingAuthoring.Status(level, thing));
        switch (change)
        {
            case "geometry": foreach (var c in wall.Corners) c.Vertex.Position += new Vec3(.5, 0, 0); break;
            case "light-flags": level.Lights[0].Flags = LightFlags.NoBlock; break;
            case "portal": wall.Adjoin = other.Adjoin = null; break;
            case "portal-flags": wall.AdjoinFlags = AdjoinFlags.BlockLight; break;
            case "surface-flags": wall.SurfFlags = 1; break;
            case "sector-flags": wall.Sector.Flags = 1; break;
        }
        Assert.NotEqual(provenance.InputSha256, ArchitecturalLightingAuthoring.InputsDigest(level, thing, Profile));
        Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(level, thing));
        Assert.Same(provenance, thing.ArchLightingProvenance);
    }

    [Fact]
    public void FirstApplyRefusesWorldDriftAndUndoRedoPreserveCapturedSamples()
    {
        var (level, thing) = Scene(); var wall = Wall(level, 1);
        var samples = Samples(level);
        var command = new ReplaceThingArchitectureCommand(level, thing, samples, "fixture.3do", Provenance(level, thing, samples));
        foreach (var c in wall.Corners) c.Vertex.Position += new Vec3(2, 0, 0);
        var history = new EditHistory();
        Assert.Throws<InvalidOperationException>(() => history.Do(command));
        Assert.Empty(level.ArchitecturalLighting); Assert.Equal(-1, thing.ArchLightingIndex); Assert.False(history.CanUndo);
        foreach (var c in wall.Corners) c.Vertex.Position -= new Vec3(2, 0, 0);
        history.Do(command); Assert.Equal(default, level.ArchitecturalLighting[0].Meshes[0].Vertices[0]);
        history.Undo(); Assert.Empty(level.ArchitecturalLighting); Assert.Null(thing.ArchLightingProvenance);
        foreach (var c in wall.Corners) c.Vertex.Position += new Vec3(2, 0, 0);
        history.Redo(); Assert.Equal(default, level.ArchitecturalLighting[0].Meshes[0].Vertices[0]);
        Assert.Equal("stale", ArchitecturalLightingAuthoring.Status(level, thing));
    }

    [Theory]
    [InlineData("nonfinite")]
    [InlineData("nonplanar")]
    [InlineData("zero-edge")]
    [InlineData("zero-area")]
    [InlineData("few-corners")]
    [InlineData("foreign-adjoin")]
    [InlineData("one-way-adjoin")]
    public void MalformedWorldRefusesEvenWhenSourceBypassesShadows(string defect)
    {
        var (level, thing) = Scene(); var wall = Wall(level, 1);
        level.Lights[0].Flags = LightFlags.NoBlock;
        switch (defect)
        {
            case "nonfinite": wall.Corners[0].Vertex.Position = new(double.NaN, 0, 0); break;
            case "nonplanar": wall.Corners[0].Vertex.Position += new Vec3(.1, 0, 0); break;
            case "zero-edge": wall.Corners[0].Vertex.Position = wall.Corners[1].Vertex.Position; break;
            case "zero-area": foreach (var c in wall.Corners) c.Vertex.Position = Vec3.Zero; break;
            case "few-corners": wall.Corners.RemoveRange(1, 3); break;
            case "foreign-adjoin": wall.Adjoin = Wall(new Level(), 1); wall.Adjoin.Adjoin = wall; break;
            case "one-way-adjoin": wall.Adjoin = Wall(level, 1); break;
        }
        Assert.Throws<InvalidOperationException>(() => Samples(level));
        Assert.Throws<InvalidOperationException>(() => ArchitecturalLightingAuthoring.InputsDigest(level, thing, Profile));
        Assert.Empty(level.ArchitecturalLighting); Assert.Null(thing.ArchLightingProvenance);
    }

    [Fact]
    public void NewProfileKeepsPairBudgetAndRejectsUnknownProfiles()
    {
        var (level, thing) = Scene();
        var error = Assert.Throws<InvalidOperationException>(() => ArchitecturalLightingAuthoring.PrepareSamples(level,
            new IReadOnlyList<Vec3>[] { new CountOnlyPoints() }, Profile));
        Assert.Contains("vertex/light-pair budget", error.Message);
        Assert.Throws<ArgumentException>(() => ArchitecturalLightingAuthoring.PrepareSamples(level, [], "invented"));
        Assert.Throws<ArgumentException>(() => ArchitecturalLightingAuthoring.InputsDigest(level, thing, "invented"));
    }

    private sealed class CountOnlyPoints : IReadOnlyList<Vec3>
    {
        public int Count => checked((int)ArchitecturalLightingAuthoring.MaxSampleLightPairs + 1);
        public Vec3 this[int index] => throw new InvalidOperationException("Over-budget points must not be read.");
        public IEnumerator<Vec3> GetEnumerator() => throw new InvalidOperationException("Over-budget points must not be read.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static (Level Level, Thing Thing) Scene()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var thing = level.NewThing(); thing.Values["model3d"] = "fixture.3do"; thing.Values["thingflags"] = "0x8";
        level.Lights.Add(new Light { Position = Vec3.Zero, Range = 4, Intensity = .8, RgbIntensity = .4, Color = new(1, .5f, .25f) });
        return (level, thing);
    }
    private static Surface Wall(Level level, double x)
    {
        var sector = level.NewSector(); var wall = sector.NewSurface();
        foreach (var p in new[] { new Vec3(x, -1, -1), new(x, 1, -1), new(x, 1, 1), new(x, -1, 1) })
            wall.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(p) });
        return wall; // Deliberately leave normal cache untouched.
    }
    private static ArchitecturalLightObject Samples(Level level, string profile = Profile) =>
        ArchitecturalLightingAuthoring.PrepareSamples(level, new IReadOnlyList<Vec3>[] { new[] { new Vec3(2, 0, 0) } }, profile);
    private static ArchitecturalLightVertex One(Level level) => Samples(level).Meshes[0].Vertices[0];
    private static ArchitectureLightingProvenance Provenance(Level level, Thing thing, ArchitecturalLightObject samples) =>
        new(Profile, "fixture.3do", new string('a', 64), ArchitecturalLightingAuthoring.InputsDigest(level, thing, Profile), ArchitecturalLightingAuthoring.SamplesDigest(samples));
}

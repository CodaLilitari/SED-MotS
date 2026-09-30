using Sed.Core;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Tests;

public sealed class PlacementArrayTests
{
    private static Level Room()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.Templates.Add("crate", new Template { Name = "crate" });
        return level;
    }

    [Fact]
    public void GridUsesUFastestAndCopiesItsResults()
    {
        var points = PlacementArrayLayout.Grid(new(-.5, -.5, 0), new(.5, 0, 0), new(0, .5, 0), 3, 2);
        Assert.Equal(new[] { new Vec3(-.5, -.5, 0), new(0, -.5, 0), new(.5, -.5, 0),
            new(-.5, 0, 0), Vec3.Zero, new(.5, 0, 0) }, points);
        Assert.False(points is Vec3[]);
    }

    [Fact]
    public void RadialUsesDocumentedBasisAndExcludesPartialAndFullSweepEndpoints()
    {
        var full = PlacementArrayLayout.Radial(Vec3.Zero, new(0, 0, 2), .5, 4, 0, 360);
        AssertNear(new(.5, 0, 0), full[0]); AssertNear(new(0, .5, 0), full[1]);
        AssertNear(new(-.5, 0, 0), full[2]); AssertNear(new(0, -.5, 0), full[3]);
        var partial = PlacementArrayLayout.Radial(Vec3.Zero, new(0, 0, -double.Epsilon), .5, 2, 0, 180);
        AssertNear(new(.5, 0, 0), partial[0]); AssertNear(new(0, -.5, 0), partial[1]);
        Assert.Equal(full, PlacementArrayLayout.Radial(Vec3.Zero, new(0, 0, 2), .5, 4, 0, 360));
    }

    [Fact]
    public void LayoutRefusesOverflowNonfiniteDegenerateAndOverBudgetResults()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlacementArrayLayout.Linear(Vec3.Zero, new(1, 0, 0), 257));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlacementArrayLayout.Grid(Vec3.Zero, new(1, 0, 0), new(0, 1, 0), 17, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlacementArrayLayout.Linear(new(999999, 0, 0), new(2, 0, 0), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlacementArrayLayout.Linear(Vec3.Zero, new(double.NaN, 0, 0), 1));
        Assert.Throws<ArgumentException>(() => PlacementArrayLayout.Linear(Vec3.Zero, Vec3.Zero, 2));
        Assert.Throws<ArgumentException>(() => PlacementArrayLayout.Grid(Vec3.Zero, new(1, 0, 0), new(1, 0, 0), 2, 2));
        Assert.Throws<ArgumentException>(() => PlacementArrayLayout.Radial(Vec3.Zero, Vec3.Zero, .5, 4, 0, 360));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlacementArrayLayout.Radial(Vec3.Zero, new(0, 0, 1), .5, 4, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlacementArrayLayout.Radial(new(1000000, 0, 0), new(0, 0, 1), 1, 4, 0, 360));
    }

    [Fact]
    public void ThingArrayIsFreshAppendWithExactUndoAndRedoWithoutRenumberingExistingThings()
    {
        var level = Room(); level.Layers.AddRange(["Main", "Props"]);
        var original = level.NewThing(); original.Num = 91; original.Name = "original"; original.Template = "crate";
        original.Sector = level.Sectors[0]; original.Values.Add("cog", "123"); original.ArchLightingIndex = 17;
        int capacity = level.ThingCapacity;
        var command = new CreateThingArrayCommand(level, PlacementArrayLayout.Linear(new(-.5, 0, 0), new(.5, 0, 0), 3),
            "crate", "lamp", orientation: new(10, 20, 30), layer: 1);
        Assert.Single(level.Things);
        command.Apply();
        Assert.Equal(91, original.Num); Assert.Same(original, level.Things[0]);
        Assert.Equal(new[] { "lamp_000", "lamp_001", "lamp_002" }, level.Things.Skip(1).Select(thing => thing.Name));
        Assert.Equal(new[] { 1, 2, 3 }, level.Things.Skip(1).Select(thing => thing.Num));
        Assert.All(level.Things.Skip(1), thing => {
            Assert.Same(level.Sectors[0], thing.Sector); Assert.Equal(1, thing.Layer);
            Assert.Equal((10d, 20d, 30d), (thing.Pitch, thing.Yaw, thing.Roll));
            Assert.Empty(thing.Values); Assert.Empty(thing.SourceParameters); Assert.Equal(-1, thing.ArchLightingIndex);
        });
        var added = level.Things.Skip(1).ToArray();
        command.Revert();
        Assert.Same(original, Assert.Single(level.Things)); Assert.Equal(91, original.Num); Assert.Equal(capacity, level.ThingCapacity);
        Assert.Equal("123", original.Values["cog"]); Assert.Equal(17, original.ArchLightingIndex);
        command.Apply(); Assert.Equal(added, level.Things.Skip(1)); command.Revert();
    }

    [Fact]
    public void ThingPlacementRequiresEveryPointInsideAndRejectsBoundaryWithoutPartialMutation()
    {
        var level = Room();
        Assert.Throws<InvalidOperationException>(() => new CreateThingArrayCommand(level,
            PlacementArrayLayout.Linear(Vec3.Zero, new(1, 0, 0), 2), "crate", "bad"));
        Assert.Throws<InvalidOperationException>(() => new CreateThingArrayCommand(level,
            PlacementArrayLayout.Linear(Vec3.Zero, new(2, 0, 0), 2), "crate", "bad", level.Sectors[0]));
        Assert.Empty(level.Things);
        var command = new CreateThingArrayCommand(level, PlacementArrayLayout.Linear(Vec3.Zero, new(.5, 0, 0), 2), "crate", "ok");
        level.Sectors[0].Surfaces.RemoveAt(0); // Geometry changed after preparation.
        Assert.Throws<InvalidOperationException>(() => command.Apply()); Assert.Empty(level.Things);
    }

    [Fact]
    public void ExplicitSectorResolvesOverlapButDoesNotPermitOutsideOrBoundary()
    {
        var level = Room();
        var other = SectorFactory.CreateBox(level, Vec3.Zero, 1, string.Empty, -1);
        new CreateSectorCommand(level, other).Apply();
        var points = PlacementArrayLayout.Linear(Vec3.Zero, new(.5, 0, 0), 2);
        Assert.Throws<InvalidOperationException>(() => new CreateThingArrayCommand(level, points, "crate", "auto"));
        var command = new CreateThingArrayCommand(level, points, "crate", "explicit", other);
        command.Apply(); Assert.All(level.Things, thing => Assert.Same(other, thing.Sector));
        Assert.Throws<InvalidOperationException>(() => new CreateThingArrayCommand(level, [new(1, 0, 0)], "crate", "wall", other));
    }

    [Fact]
    public void CapacityNamesAndLayerAreValidatedBeforeAnyInsertionAndAgainAtApply()
    {
        var level = Room(); var points = PlacementArrayLayout.Linear(Vec3.Zero, new(.5, 0, 0), 2);
        level.ThingCapacity = 1;
        Assert.Throws<InvalidOperationException>(() => new CreateThingArrayCommand(level, points, "crate", "cap"));
        Assert.Empty(level.Things); Assert.Equal(1, level.ThingCapacity);
        level.ThingCapacity = 8;
        Assert.Throws<ArgumentException>(() => new CreateThingArrayCommand(level, points, "missing", "cap"));
        Assert.Throws<ArgumentException>(() => new CreateThingArrayCommand(level, points, "crate", new string('x', 124)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CreateThingArrayCommand(level, points, "crate", "cap", layer: 1));
        var command = new CreateThingArrayCommand(level, points, "crate", "test");
        var colliding = level.NewThing(); colliding.Name = "TEST_001"; colliding.Num = 800;
        Assert.Throws<InvalidOperationException>(() => command.Apply());
        Assert.Same(colliding, Assert.Single(level.Things)); Assert.Equal(800, colliding.Num);
    }

    [Theory]
    [InlineData(null)] [InlineData(0d)] [InlineData(1d)]
    public void LightArrayKeepsStoredIndependentChannelAndExactUndo(double? independent)
    {
        var level = Room(); level.Layers.AddRange(["Main", "Accent"]);
        var old = level.NewLight(); old.Num = 71; old.RgbIntensity = 0;
        var command = new CreateLightArrayCommand(level, PlacementArrayLayout.Linear(Vec3.Zero, new(2, 0, 0), 3),
            new(.25f, .5f, .75f), 1, 8, independent, -5, 0x80000001L, 1);
        Assert.Single(level.Lights); command.Apply();
        Assert.All(level.Lights.Skip(1), light => {
            Assert.Equal(independent, light.RgbIntensity); Assert.Equal(independent ?? 1, light.EffectiveRgbIntensity);
            Assert.Equal(-5, light.LegacyRgbRange); Assert.Equal(0x80000001L, light.Flags); Assert.Equal(1, light.Layer);
        });
        Assert.Equal(71, old.Num); command.Revert();
        Assert.Same(old, Assert.Single(level.Lights)); Assert.Equal(71, old.Num); Assert.Equal(0, old.RgbIntensity);
        command.Apply(); command.Revert(); Assert.Single(level.Lights);
    }

    [Fact]
    public void LightArrayRejectsInvalidRgbFlagsAndJkColorWithoutMutation()
    {
        var level = Room(); var points = new[] { Vec3.Zero };
        Assert.Throws<ArgumentOutOfRangeException>(() => new CreateLightArrayCommand(level, points, ColorF.White, double.NaN, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CreateLightArrayCommand(level, points, ColorF.White, 1, 1, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CreateLightArrayCommand(level, points, ColorF.White, 1, 1, flags: -1));
        level.Kind = ProjectType.JediKnight;
        Assert.Throws<ArgumentException>(() => new CreateLightArrayCommand(level, points, new(.5f, 1, 1), 1, 1));
        Assert.Throws<ArgumentException>(() => new CreateLightArrayCommand(level, points, ColorF.White, 1, 1, 0));
        Assert.Empty(level.Lights);
        new CreateLightArrayCommand(level, points, ColorF.White, 1, 1).Apply(); Assert.Single(level.Lights);
    }

    [Fact]
    public void UndoRejectsUnexpectedSuffixWithoutRemovingOtherObjects()
    {
        var level = Room(); var command = new CreateLightArrayCommand(level, [Vec3.Zero], ColorF.White, 1, 1);
        command.Apply(); var unrelated = level.NewLight(); var snapshot = level.Lights.ToArray();
        Assert.Throws<InvalidOperationException>(() => command.Revert()); Assert.Equal(snapshot, level.Lights);
        level.Lights.Remove(unrelated); command.Revert(); Assert.Empty(level.Lights);
    }

    [Fact]
    public void MutatingNonFirstThingPayloadCannotBypassPlacementTemplateOrArchitectureChecks()
    {
        foreach (var mutate in new Action<Thing>[] {
            thing => thing.Position = new(100, 0, 0), thing => thing.Template = "missing",
            thing => thing.Pitch = double.NaN, thing => thing.ArchLightingIndex = 0,
            thing => thing.Values["cog"] = "42", thing => thing.Name = "renamed",
        })
        {
            var level = Room();
            var command = new CreateThingArrayCommand(level, PlacementArrayLayout.Linear(Vec3.Zero, new(.5, 0, 0), 2), "crate", "test");
            mutate(command.Things[1]);
            Assert.Throws<InvalidOperationException>(() => command.Apply()); Assert.Empty(level.Things);
        }
    }

    [Fact]
    public void MutatingLightPayloadCannotIntroduceNanOrDifferentSemanticsBeforeApply()
    {
        foreach (var mutate in new Action<Light>[] {
            light => light.Position = new(double.NaN, 0, 0), light => light.Intensity = double.NaN,
            light => light.RgbIntensity = 0, light => light.Flags = -1, light => light.Layer = 1,
        })
        {
            var level = Room();
            var command = new CreateLightArrayCommand(level, [Vec3.Zero, new(.5, 0, 0)], ColorF.White, 1, 1);
            mutate(command.Lights[1]);
            Assert.Throws<InvalidOperationException>(() => command.Apply()); Assert.Empty(level.Lights);
        }
    }

    [Fact]
    public void ExistingCogsRequireReferenceGuardAndGuardRunsOnEachApplyIncludingRedo()
    {
        var level = Room(); level.Cogs.Add(new Cog { Name = "links.cog" });
        Assert.Throws<InvalidOperationException>(() => new CreateThingArrayCommand(level, [Vec3.Zero], "crate", "test"));
        int checks = 0; bool accept = true;
        void Guard() { checks++; if (!accept) throw new InvalidOperationException("Changed COG references."); }
        var command = new CreateThingArrayCommand(level, [Vec3.Zero], "crate", "test", validateReferenceClosure: Guard);
        Assert.Equal(1, checks); command.Apply(); Assert.Equal(2, checks); command.Revert();
        accept = false; Assert.Throws<InvalidOperationException>(() => command.Apply()); Assert.Equal(3, checks);
        Assert.Empty(level.Things);
    }

    private static void AssertNear(Vec3 expected, Vec3 actual) => Assert.True(expected.DistanceTo(actual) < 1e-12, $"Expected {expected}; got {actual}.");
}

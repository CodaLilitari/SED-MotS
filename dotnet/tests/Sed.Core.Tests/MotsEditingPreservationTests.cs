using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Xunit;

namespace Sed.Core.Tests;

public class MotsEditingPreservationTests
{
    private static (Level level, Sector sector) Room()
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var sector = SectorFactory.CreateBox(level, Vec3.Zero, 1, string.Empty, -1);
        level.Sectors.Add(sector);
        foreach (var c in sector.Surfaces.SelectMany(s => s.Corners))
        { c.MonoIntensity = 0.85f; c.Intensity = new ColorF(0.1f, 0.2f, 0.3f); }
        return (level, sector);
    }

    [Fact]
    public void SurfaceCleavePreservesIndependentMonoAtOldAndNewCorners()
    {
        var (_, sector) = Room();
        var surface = sector.Surfaces.First(s => s.Corners.Any(c => c.Vertex.Position.X < 0) && s.Corners.Any(c => c.Vertex.Position.X > 0));
        var command = new CleaveSurfaceCommand(surface, new Vec3(1, 0, 0), Vec3.Zero);
        command.Apply();
        Assert.Equal(7, sector.Surfaces.Count);
        Assert.All(sector.Surfaces.SelectMany(s => s.Corners), c => Assert.Equal(0.85f, c.MonoIntensity));
        command.Revert();
        Assert.Equal(6, sector.Surfaces.Count);
        command.Apply();
        Assert.All(sector.Surfaces.SelectMany(s => s.Corners), c => Assert.Equal(0.85f, c.MonoIntensity));
    }

    [Fact]
    public void LightingUndoRestoresAuthoredMonoAndRgb()
    {
        var (level, sector) = Room();
        var corner = sector.Surfaces[0].Corners[0];
        var set = new SetVertexLightCommand(sector, corner.Vertex, ColorF.White);
        set.Apply(); Assert.Equal(1f, corner.MonoIntensity);
        set.Revert(); Assert.Equal(0.85f, corner.MonoIntensity); Assert.Equal(0.3f, corner.Intensity.B);
        var bake = new CalculateLightingCommand(level);
        bake.Apply(); Assert.Equal(0f, corner.MonoIntensity);
        bake.Revert(); Assert.Equal(0.85f, corner.MonoIntensity); Assert.Equal(0.3f, corner.Intensity.B);
        bake.Apply(); Assert.Equal(0f, corner.MonoIntensity);
    }

    [Fact]
    public void EditingRedPreservesIndependentMonoAndOtherCornerChannels()
    {
        var (_, sector) = Room(); var corner = sector.Surfaces[0].Corners[0];
        var related = sector.Surfaces.SelectMany(s => s.Corners).Where(c => c.Vertex == corner.Vertex).ToArray();
        related[^1].Intensity = new ColorF(.4f, .5f, .6f);
        var edit = new SetVertexLightComponentCommand(sector, corner.Vertex, VertexLightComponent.Red, .7f);
        edit.Apply(); Assert.All(related, c => { Assert.Equal(.7f, c.Intensity.R); Assert.Equal(.85f, c.MonoIntensity); });
        Assert.Equal(.2f, related[0].Intensity.G); Assert.Equal(.6f, related[^1].Intensity.B);
        edit.Revert(); Assert.Equal(.1f, related[0].Intensity.R); Assert.Equal(.4f, related[^1].Intensity.R);
    }

    [Fact]
    public void SurfaceCleaveRefusesToSplitOnlyOneSideOfAPortal()
    {
        var (_, sector) = Room();
        var surface = sector.Surfaces.First(s => s.Corners.Any(c => c.Vertex.Position.X < 0) && s.Corners.Any(c => c.Vertex.Position.X > 0));
        var partner = new Surface(sector); surface.Adjoin = partner; partner.Adjoin = surface;
        var corners = surface.Corners.ToArray();
        var command = new CleaveSurfaceCommand(surface, new Vec3(1, 0, 0), Vec3.Zero);
        command.Apply(); Assert.NotNull(command.FailureReason); Assert.Equal(corners, surface.Corners);
        Assert.Equal(6, sector.Surfaces.Count); Assert.Same(partner, surface.Adjoin);
    }

    [Fact]
    public void ClipboardSnapshotsArchitectureAndRemapsItAcrossDocumentsWithUndo()
    {
        var (source, sector) = Room();
        sector.BoundBox = new Box(new(-2, -2, -2), new(2, 2, 2));
        sector.Center = Vec3.Zero; sector.Radius = 3;
        sector.BoundsSourceVertices = sector.Vertices.Select(v => v.Position).ToArray();
        var baked = new ArchitecturalLightObject();
        var mesh = new ArchitecturalLightMesh();
        mesh.Vertices.Add(new(.9, .1, .2, .3)); baked.Meshes.Add(mesh); source.ArchitecturalLighting.Add(baked);
        var thing = source.NewThing(); thing.Sector = sector; thing.ArchLightingIndex = 0; thing.ArchLightingSourceModel = "fixture.3do";
        var selection = new SelectionSet(); selection.Add(sector); selection.Add(thing);
        var fragment = LevelFragment.Capture(selection, source);
        mesh.Vertices[0] = new(99, 99, 99, 99);
        var target = new Level { Kind = ProjectType.MysteriesOfTheSith }; target.ArchitecturalLighting.Add(new ArchitecturalLightObject());
        var paste = new PasteFragmentCommand(target, fragment, new Vec3(10, 0, 0));
        paste.Apply();
        Assert.Equal(1, target.Things[0].ArchLightingIndex);
        Assert.Equal(.9, target.ArchitecturalLighting[1].Meshes[0].Vertices[0].Mono);
        Assert.Same(target.Sectors[0], target.Things[0].Sector);
        Assert.Equal(8, target.Sectors[0].BoundBox!.Value.Min.X);
        Assert.All(target.Sectors[0].Surfaces.SelectMany(s => s.Corners), c => Assert.Equal(.85f, c.MonoIntensity));
        paste.Revert(); Assert.Empty(target.Sectors); Assert.Empty(target.Things); Assert.Single(target.ArchitecturalLighting);
        paste.Apply(); Assert.Equal(.9, target.ArchitecturalLighting[1].Meshes[0].Vertices[0].Mono);
    }

    [Fact]
    public void ThingOnlyPasteNeverKeepsASectorFromAnotherDocument()
    {
        var (level, sector) = Room();
        var thing = level.NewThing(); thing.Sector = sector;
        var selection = new SelectionSet(); selection.Add(thing);
        var target = new Level { Kind = ProjectType.MysteriesOfTheSith };
        new PasteFragmentCommand(target, LevelFragment.Capture(selection, level), Vec3.Zero).Apply();
        Assert.Null(target.Things[0].Sector);
    }

    [Fact]
    public void CrossGamePasteIsRejectedBeforeAnyMutation()
    {
        var (source, sector) = Room(); var selection = new SelectionSet(); selection.Add(sector);
        var target = new Level { Kind = ProjectType.JediKnight };
        var paste = new PasteFragmentCommand(target, LevelFragment.Capture(selection, source), Vec3.Zero);
        Assert.Throws<InvalidOperationException>(() => paste.Apply());
        Assert.Empty(target.Sectors); Assert.Empty(target.Things); Assert.Empty(target.ArchitecturalLighting);
    }

    [Fact]
    public void IndependentRedEditFreezesImplicitMonoAndUndoRestoresNull()
    {
        var (_, sector) = Room(); var corner = sector.Surfaces[0].Corners[0]; corner.MonoIntensity = null;
        var edit = new SetVertexLightComponentCommand(sector, corner.Vertex, VertexLightComponent.Red, .7f);
        edit.Apply(); Assert.Equal(.1f, corner.MonoIntensity); Assert.Equal(.7f, corner.Intensity.R);
        edit.Revert(); Assert.Null(corner.MonoIntensity); Assert.Equal(.1f, corner.Intensity.R);
    }

    [Fact]
    public void ExplicitArchitectureUnbindCanBeUndoneWithoutDeletingItsArrays()
    {
        var (level, _) = Room();
        var original = new ArchitecturalLightObject(); level.ArchitecturalLighting.Add(original);
        var thing = level.NewThing(); thing.ArchLightingIndex = 0;
        thing.ArchLightingSourceModel = "old.3do"; thing.ArchLightingSourceModelHash = new string('a', 64);
        var command = new ClearThingArchitectureCommand(thing);
        command.Apply(); Assert.Equal(-1, thing.ArchLightingIndex); Assert.Empty(thing.ArchLightingSourceModelHash);
        Assert.Same(original, Assert.Single(level.ArchitecturalLighting));
        command.Revert(); Assert.Equal(0, thing.ArchLightingIndex); Assert.Equal(new string('a', 64), thing.ArchLightingSourceModelHash);
    }

    private sealed class ActionCommand(Action apply, Action revert) : IEditCommand
    { public string Name => "Test"; public void Apply() => apply(); public void Revert() => revert(); }

    [Fact]
    public void RejectedCompositeRollsBackCompletedChildrenAndPreservesHistory()
    {
        int state = 0;
        var history = new EditHistory();
        var successful = new ActionCommand(() => state++, () => state--);
        history.Do(successful); history.Undo();
        var fail = new ActionCommand(() => throw new InvalidOperationException("rejected"), () => { });
        Assert.Throws<InvalidOperationException>(() => history.Do(new CompositeCommand("transaction", [successful, fail])));
        Assert.Equal(0, state); Assert.False(history.CanUndo); Assert.True(history.CanRedo);
        history.Redo(); Assert.Equal(1, state);
    }

    [Fact]
    public void FailedUndoKeepsTheCommandAvailableForRetry()
    {
        bool fail = true; int state = 0;
        var history = new EditHistory();
        history.Do(new ActionCommand(() => state++, () => { if (fail) throw new InvalidOperationException(); state--; }));
        Assert.Throws<InvalidOperationException>(() => history.Undo());
        Assert.True(history.CanUndo); Assert.False(history.CanRedo); Assert.Equal(1, state);
        fail = false; history.Undo(); Assert.Equal(0, state);
    }
}

using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;
using Xunit;

namespace Sed.Core.Tests;

public class ClipboardDependencyTests
{
    private static Level LevelWithLayers(params string[] layers)
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.Layers.AddRange(layers);
        return level;
    }

    private static Sector AddRoom(Level level, int layer = 0)
    {
        var room = SectorFactory.CreateBox(level, Vec3.Zero, 1, string.Empty, -1);
        room.Layer = layer; level.Sectors.Add(room); level.RenumberSectors();
        return room;
    }

    private static Template AddTemplate(Level level, string name, string parent = "none", params (string key, string value)[] parameters)
    {
        var template = new Template { Name = name, Parent = parent, Order = level.Templates.Count };
        foreach (var (key, value) in parameters)
        { template.Values[key] = value; template.SourceParameters.Add(new(key, value)); }
        level.Templates.Add(name, template);
        return template;
    }

    private static LevelFragment SelectThing(Level source, string template)
    {
        var thing = source.NewThing(); thing.Template = template;
        var selection = new SelectionSet(); selection.Add(thing);
        return LevelFragment.Capture(selection, source);
    }

    [Fact]
    public void LayersRemapByCapturedNamesForSectorsThingsAndLightsAndUndoExactly()
    {
        var source = LevelWithLayers("Main", "Shared", "Upper");
        var room = AddRoom(source, 2);
        var thing = source.NewThing(); thing.Sector = room; thing.Layer = 1;
        var light = source.NewLight(); light.Layer = 0;
        var selection = new SelectionSet(); selection.Add(room); selection.Add(thing); selection.Add(light);
        var fragment = LevelFragment.Capture(selection, source);
        source.Layers[2] = "Renamed after copying";
        var target = LevelWithLayers("Shared", "Main");
        var history = new EditHistory(); var paste = new PasteFragmentCommand(target, fragment, Vec3.Zero);
        history.Do(paste);
        Assert.Equal(new[] { "Shared", "Main", "Upper" }, target.Layers);
        Assert.Equal(2, target.Sectors[0].Layer); Assert.Equal(0, target.Things[0].Layer); Assert.Equal(1, target.Lights[0].Layer);
        var back = JklParser.Parse(JklWriter.Build(new JklDocument(target)));
        Assert.Equal(target.Layers, back.Layers); Assert.Equal(2, back.Sectors[0].Layer); Assert.Equal(0, back.Things[0].Layer);
        var pastedRoom = target.Sectors[0];
        history.Undo(); Assert.Equal(new[] { "Shared", "Main" }, target.Layers); Assert.Empty(target.Sectors);
        history.Redo(); Assert.Same(pastedRoom, target.Sectors[0]); Assert.Equal("Upper", target.Layers[2]);
    }

    [Fact]
    public void DuplicateLayerNamesDoNotCollapseDistinctSourceLayers()
    {
        var source = LevelWithLayers("Same", "Same");
        var a = AddRoom(source, 0); var b = AddRoom(source, 1);
        var selection = new SelectionSet(); selection.Add(a); selection.Add(b);
        var target = LevelWithLayers("Same");
        new PasteFragmentCommand(target, LevelFragment.Capture(selection, source), Vec3.Zero).Apply();
        Assert.Equal(new[] { "Same", "Same" }, target.Layers);
        Assert.Equal(new[] { 0, 1 }, target.Sectors.Select(sector => sector.Layer));
    }

    [Fact]
    public void TemplateParentChainsAreSnapshotsAppendedInOrderWithRepeatedValuesAndUndo()
    {
        var source = LevelWithLayers("Actors");
        var root = AddTemplate(source, "base", "none", ("model3d", "source.3do"));
        AddTemplate(source, "middle", "base", ("mass", "3"));
        var child = AddTemplate(source, "actor", "middle", ("frame", "(0/0/0:0/0/0)"), ("frame", "(1/2/3:0/0/0)"));
        var fragment = SelectThing(source, "actor");
        root.Values["model3d"] = "edited.3do"; child.SourceParameters.Clear();
        var target = LevelWithLayers("Main"); var existing = AddTemplate(target, "unrelated"); existing.Order = 20;
        var history = new EditHistory(); var paste = new PasteFragmentCommand(target, fragment, Vec3.Zero);
        history.Do(paste);
        Assert.Equal("source.3do", target.GetThingModel(target.Things[0]));
        Assert.Equal(2, target.Templates["actor"].SourceParameters.Count);
        Assert.True(target.Templates["base"].Order > existing.Order);
        Assert.True(target.Templates["middle"].Order > target.Templates["base"].Order);
        Assert.True(target.Templates["actor"].Order > target.Templates["middle"].Order);
        var back = JklParser.Parse(JklWriter.Build(new JklDocument(target)));
        Assert.Equal(2, back.Templates["actor"].SourceParameters.Count);
        Assert.Equal("source.3do", back.GetThingModel(back.Things[0]));
        var pastedTemplate = target.Templates["actor"];
        history.Undo(); Assert.Single(target.Templates); Assert.Same(existing, target.Templates["unrelated"]); Assert.Single(target.Layers);
        history.Redo(); Assert.Same(pastedTemplate, target.Templates["actor"]);
        Assert.Equal("source.3do", source.Templates["base"].SourceParameters[0].Value);
    }

    [Fact]
    public void CompatibleExistingParentsAreReusedAndOnlyAddedChildrenAreUndone()
    {
        var source = LevelWithLayers("Main"); AddTemplate(source, "base", "none", ("mass", "3")); AddTemplate(source, "actor", "base");
        var target = LevelWithLayers("Main"); var existing = AddTemplate(target, "base", "none", ("mass", "3"));
        var paste = new PasteFragmentCommand(target, SelectThing(source, "actor"), Vec3.Zero);
        paste.Apply(); Assert.Equal(2, target.Templates.Count); Assert.Same(existing, target.Templates["base"]);
        paste.Revert(); Assert.Single(target.Templates); Assert.Same(existing, target.Templates["base"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConflictingEffectiveOrEarlierRepeatedParametersFailBeforeAnyMutation(bool earlierRepeated)
    {
        var source = LevelWithLayers("New layer");
        AddTemplate(source, "actor", "none", ("frame", "first"), ("frame", "last"));
        var target = LevelWithLayers("Main");
        var existing = AddTemplate(target, "actor", "none", ("frame", "first"), ("frame", "last"));
        var fragment = SelectThing(source, "actor");
        Assert.Null(fragment.CompatibilityError(target));
        if (earlierRepeated) existing.SourceParameters[0] = new("frame", "changed");
        else existing.Values["frame"] = "changed";
        Assert.Contains("differs", fragment.CompatibilityError(target));
        Assert.Throws<InvalidOperationException>(() => new PasteFragmentCommand(target, fragment, Vec3.Zero).Apply());
        Assert.Equal(new[] { "Main" }, target.Layers); Assert.Empty(target.Things); Assert.Empty(target.ArchitecturalLighting);
        Assert.Same(existing, Assert.Single(target.Templates).Value);
    }

    [Fact]
    public void MissingSourceTemplateCannotResolveToAnUnrelatedTargetDefinition()
    {
        var source = LevelWithLayers("Main"); var fragment = SelectThing(source, "undefined");
        Assert.Null(fragment.CompatibilityError(source)); // preserves the existing unresolved reference in its original document
        var target = LevelWithLayers("Main");
        Assert.Contains("undefined", fragment.CompatibilityError(target));
        AddTemplate(source, "undefined", "none", ("mass", "5"));
        Assert.Contains("undefined", fragment.CompatibilityError(source));
    }

    [Fact]
    public void CyclicAndMissingLayerDependenciesAreReportedByGuiPreflight()
    {
        var source = LevelWithLayers("Main"); AddTemplate(source, "a", "b"); AddTemplate(source, "b", "a");
        Assert.Contains("cycle", SelectThing(source, "a").CompatibilityError(source));
        var room = AddRoom(source, 5); var selection = new SelectionSet(); selection.Add(room);
        Assert.Contains("missing source layer", LevelFragment.Capture(selection, source).CompatibilityError(source));
    }

    [Fact]
    public void ExistingChildWithMissingParentCannotBeFixedByAppendingParentAfterIt()
    {
        var source = LevelWithLayers("Main"); AddTemplate(source, "base"); AddTemplate(source, "actor", "base");
        var target = LevelWithLayers("Main"); AddTemplate(target, "actor", "base");
        Assert.Contains("loading order", SelectThing(source, "actor").CompatibilityError(target));
    }

    [Fact]
    public void CustomSectorColormapIsAppendedForExportAndRemovedOnUndo()
    {
        var source = LevelWithLayers("Main"); var room = AddRoom(source); room.ColorMap = "custom.cmp";
        var selection = new SelectionSet(); selection.Add(room);
        var target = LevelWithLayers("Main"); target.ColorMaps.Add("target.cmp");
        var paste = new PasteFragmentCommand(target, LevelFragment.Capture(selection, source), Vec3.Zero);
        paste.Apply(); Assert.Equal(new[] { "target.cmp", "custom.cmp" }, target.ColorMaps);
        Assert.Equal("custom.cmp", JklParser.Parse(JklWriter.Build(new JklDocument(target))).Sectors[0].ColorMap, ignoreCase: true);
        paste.Revert(); Assert.Equal(new[] { "target.cmp" }, target.ColorMaps);
        paste.Apply(); Assert.Equal("custom.cmp", target.ColorMaps[1]);
    }

    [Fact]
    public void UndoRefusesExternalDependencyChangesBeforeRemovingPastedObjects()
    {
        var source = LevelWithLayers("Source"); var room = AddRoom(source);
        var selection = new SelectionSet(); selection.Add(room); var target = LevelWithLayers("Target");
        var paste = new PasteFragmentCommand(target, LevelFragment.Capture(selection, source), Vec3.Zero);
        paste.Apply(); target.Layers.Add("External work");
        Assert.Throws<InvalidOperationException>(paste.Revert);
        Assert.Single(target.Sectors); Assert.Equal(new[] { "Target", "Source", "External work" }, target.Layers);
    }
}

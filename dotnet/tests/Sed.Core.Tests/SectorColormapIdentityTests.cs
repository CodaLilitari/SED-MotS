using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class SectorColormapIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-colormap-identity-" + Guid.NewGuid().ToString("N"));
    public SectorColormapIdentityTests() => Directory.CreateDirectory(_directory);

    private static Level DuplicateNames()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.ColorMaps.Clear();
        level.ColorMaps.AddRange(["duplicate.cmp", "duplicate.cmp", "other.cmp"]);
        level.Sectors[0].ColorMap = "duplicate.cmp";
        level.Sectors[0].ColorMapIndex = 1;
        return level;
    }

    [Fact]
    public void ExplicitEqualNameSlotChangeSupportsUndoAndRedo()
    {
        var sector = DuplicateNames().Sectors[0];
        var history = new EditHistory();
        history.Do(new SetSectorColormapCommand(sector, "duplicate.cmp", 0));
        Assert.Equal("duplicate.cmp", sector.ColorMap);
        Assert.Equal(0, sector.ColorMapIndex);
        Assert.Equal(0, sector.ResolveColorMapIndex());
        history.Undo();
        Assert.Equal(1, sector.ColorMapIndex);
        Assert.Equal(1, sector.ResolveColorMapIndex());
        history.Redo();
        Assert.Equal(0, sector.ColorMapIndex);
    }

    [Fact]
    public void NameOnlyAssignmentPreservesMatchingOriginalSlotAndClearsItForADifferentName()
    {
        var sector = DuplicateNames().Sectors[0];
        new SetSectorColormapCommand(sector, "DUPLICATE.CMP").Apply();
        Assert.Equal(1, sector.ColorMapIndex);
        Assert.Equal(1, sector.ResolveColorMapIndex());
        var change = new SetSectorColormapCommand(sector, "other.cmp");
        change.Apply();
        Assert.Equal(-1, sector.ColorMapIndex);
        Assert.Equal(2, sector.ResolveColorMapIndex());
        change.Revert();
        Assert.Equal("DUPLICATE.CMP", sector.ColorMap);
        Assert.Equal(1, sector.ResolveColorMapIndex());
    }

    [Fact]
    public void AmbiguousNameWithoutMatchingSlotRefusesInsteadOfSelectingTheFirstDuplicate()
    {
        var sector = DuplicateNames().Sectors[0];
        sector.ColorMapIndex = -1;
        Assert.Contains("ambiguous", Assert.Throws<InvalidOperationException>(() => sector.ResolveColorMapIndex()).Message);
        Assert.Throws<InvalidOperationException>(() => new SetSectorColormapCommand(sector, "duplicate.cmp"));
        Assert.Equal(-1, sector.ColorMapIndex);
        var explicitSelection = new SetSectorColormapCommand(sector, "duplicate.cmp", 1);
        explicitSelection.Apply();
        Assert.Equal(1, sector.ResolveColorMapIndex());
    }

    [Fact]
    public void MismatchedExplicitSlotRefusesAndStaleSlotNeverOverridesAUniqueName()
    {
        var sector = DuplicateNames().Sectors[0];
        Assert.Throws<ArgumentException>(() => new SetSectorColormapCommand(sector, "duplicate.cmp", 2));
        Assert.Equal(1, sector.ColorMapIndex);
        Assert.Equal("duplicate.cmp", sector.ColorMap);
        sector.ColorMap = "other.cmp";
        Assert.Equal(2, sector.ResolveColorMapIndex());
        sector.ColorMapIndex = 0;
        sector.Level.ColorMaps.Clear();
        Assert.Equal(0, sector.ResolveColorMapIndex()); // Legacy unbound scaffold convention.
    }

    [Theory]
    [InlineData("extrude")]
    [InlineData("cleave")]
    [InlineData("same-level-paste")]
    public void SameLevelGeometryCommandsRetainNumericColormapBinding(string operation)
    {
        var level = DuplicateNames();
        var sector = level.Sectors[0];
        var selection = new SelectionSet();
        selection.Add(sector);
        IEditCommand command = operation switch
        {
            "extrude" => new ExtrudeSurfaceCommand(sector.Surfaces[5], 2),
            "cleave" => new CleaveSectorCommand(level, sector, new(1, 0, 0), Vec3.Zero),
            "same-level-paste" => new PasteFragmentCommand(level, LevelFragment.Capture(selection, level), new(4, 0, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        var history = new EditHistory();
        history.Do(command);
        Assert.Equal(2, level.Sectors.Count);
        Assert.All(level.Sectors, item => Assert.Equal(1, item.ColorMapIndex));
        var parsed = JklParser.ParseDocument(JklWriter.Build(new JklDocument(level)));
        Assert.All(parsed.Level.Sectors, item => Assert.Equal(1, item.ColorMapIndex));
        history.Undo();
        Assert.Single(level.Sectors);
        Assert.Equal(1, sector.ColorMapIndex);
        history.Redo();
        Assert.Equal(2, level.Sectors.Count);
        Assert.All(level.Sectors, item => Assert.Equal(1, item.ResolveColorMapIndex()));
    }

    [Fact]
    public void CrossLevelClipboardDiscardsSourceLocalSlotAndResolvesUniqueDestinationName()
    {
        var source = DuplicateNames();
        var selection = new SelectionSet();
        selection.Add(source.Sectors[0]);
        var fragment = LevelFragment.Capture(selection, source);
        Assert.Equal(1, fragment.Sectors[0].ColorMapIndex);
        var destination = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        destination.ColorMaps.AddRange(["first.cmp", "second.cmp", "duplicate.cmp"]);
        var paste = new PasteFragmentCommand(destination, fragment, new(5, 0, 0));
        paste.Apply();
        var pasted = Assert.Single(paste.PastedSectors);
        Assert.Equal(-1, pasted.ColorMapIndex);
        Assert.Equal("duplicate.cmp", pasted.ColorMap);
        Assert.Equal(2, pasted.ResolveColorMapIndex());
        var parsed = JklParser.ParseDocument(JklWriter.Build(new JklDocument(destination)));
        Assert.Equal(2, parsed.Level.Sectors[1].ColorMapIndex);
        paste.Revert();
        Assert.Single(destination.Sectors);
        paste.Apply();
        Assert.Same(pasted, Assert.Single(paste.PastedSectors));
        Assert.Equal(2, pasted.ResolveColorMapIndex());
        Assert.Equal(1, source.Sectors[0].ColorMapIndex);
    }

    [Fact]
    public void ExplicitAgentSelectionChangesDuplicateSlotAndReportsActualReparsedIdentity()
    {
        string input = Path.Combine(_directory, "source.jkl");
        File.WriteAllText(input, JklWriter.Build(new JklDocument(DuplicateNames())));
        byte[] original = File.ReadAllBytes(input);
        Assert.Equal(1, AgentWorkspace.Load(input).Document.Level.Sectors[0].ColorMapIndex);
        string request = $$"""{"schemaVersion":1,"inputSha256":"{{AgentWorkspace.Hash(original)}}","operations":[{"op":"setSectorColormap","sector":0,"colormapIndex":0}]}""";
        var plan = AgentWorkspace.Plan(input, request);
        var report = JsonSerializer.SerializeToElement(plan.Report, AgentWorkspace.JsonOptions);
        var change = report.GetProperty("changes")[0];
        Assert.Equal(1, change.GetProperty("before").GetProperty("state").GetProperty("index").GetInt32());
        Assert.Equal(0, change.GetProperty("afterOperation").GetProperty("state").GetProperty("index").GetInt32());
        Assert.Equal(0, change.GetProperty("serializedFinal").GetProperty("state").GetProperty("index").GetInt32());
        var planned = JklParser.ParseDocument(plan.OutputText);
        Assert.Equal(0, planned.Level.Sectors[0].ColorMapIndex);
        Assert.Equal(new[] { "duplicate.cmp", "duplicate.cmp", "other.cmp" }, planned.Level.ColorMaps);
        string output = Path.Combine(_directory, "selected-zero.jkl");
        AgentWorkspace.Commit(plan, output, report.GetProperty("outputSha256").GetString());
        var query = JsonSerializer.SerializeToElement(AgentWorkspace.Query(output, """{"schemaVersion":1,"collection":"sectors"}"""), AgentWorkspace.JsonOptions);
        Assert.Equal(0, query.GetProperty("items")[0].GetProperty("colorMapIndex").GetInt32());
        Assert.Equal("duplicate.cmp", query.GetProperty("items")[0].GetProperty("colorMap").GetString());
        Assert.Equal(original, File.ReadAllBytes(input));
    }

    [Fact]
    public void ReadOnlyQueryReportsInvalidStoredSlotWithoutResolvingIt()
    {
        string input = Path.Combine(_directory, "invalid-slot.jkl");
        string valid = JklWriter.Build(new JklDocument(DuplicateNames()));
        string invalid = valid.Replace("COLORMAP\t1", "COLORMAP\t99", StringComparison.Ordinal);
        Assert.NotEqual(valid, invalid);
        File.WriteAllText(input, invalid);
        var query = JsonSerializer.SerializeToElement(AgentWorkspace.Query(input, """{"schemaVersion":1,"collection":"sectors"}"""), AgentWorkspace.JsonOptions);
        Assert.Equal(99, query.GetProperty("items")[0].GetProperty("colorMapIndex").GetInt32());
        Assert.Equal(string.Empty, query.GetProperty("items")[0].GetProperty("colorMap").GetString());
        Assert.Equal(invalid, File.ReadAllText(input));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

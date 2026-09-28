using Sed.Core.Editing;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Cogs;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class PlayableSurfaceBindingTests
{
    private static readonly CogScript Script = CogScript.Parse("surface-trigger.cog",
        "symbols\nsurface trigger linkid=4\nmessage activated\nend\n");

    [Fact]
    public void AddCogBindsGlobalSurfaceSixToSecondSectorFirstSurfaceThroughSave()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        new CreateSectorCommand(level,
            SectorFactory.CreateBox(level, new(3, 0, 0), 1, string.Empty, -1)).Apply();
        Assert.Equal(2, level.Sectors.Count);
        Assert.All(level.Sectors, sector => Assert.Equal(6, sector.Surfaces.Count));

        // Reopen the scaffold before adding the COG so this exercises an actual
        // serialized global surface address, beyond either sector's local range.
        var document = JklParser.ParseDocument(JklWriter.Build(new JklDocument(level)));
        AddAndAssertThroughSave(document, globalIndex: 6, sectorIndex: 1, surfaceIndex: 0);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void AddCogUsesRetainedGlobalOrderInsteadOfSectorTraversalThroughSave(
        int globalIndex, int sectorIndex)
    {
        var document = JklParser.ParseDocument(Noncanonical);
        AddAndAssertThroughSave(document, globalIndex, sectorIndex, surfaceIndex: 0);
    }

    private static void AddAndAssertThroughSave(JklDocument document,
        int globalIndex, int sectorIndex, int surfaceIndex)
    {
        Assert.Empty(document.Level.Cogs);
        AssertAddress(CogBindings.SurfaceTargets(document)[globalIndex], globalIndex, sectorIndex, surfaceIndex);
        var command = CogBindings.PrepareAdd(document, Script.Name, Script,
            [new("trigger", CogSymbolType.Surface, globalIndex)]);
        Assert.Empty(document.Level.Cogs);
        command.Apply();
        AssertBinding(document, globalIndex, sectorIndex, surfaceIndex);

        var reopened = JklParser.ParseDocument(JklWriter.Build(document));
        AssertBinding(reopened, globalIndex, sectorIndex, surfaceIndex);
    }

    private static void AssertBinding(JklDocument document,
        int globalIndex, int sectorIndex, int surfaceIndex)
    {
        Assert.Single(document.Level.Cogs);
        var description = CogBindings.Describe(document, 0, Script);
        Assert.True(description.SchemaComplete);
        Assert.True(description.ParameterCountMatches);
        var binding = Assert.Single(description.Rows);
        Assert.Equal("trigger", binding.SymbolName);
        Assert.Equal(globalIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), binding.RawValue);
        AssertAddress(binding.Target, globalIndex, sectorIndex, surfaceIndex);
    }

    private static void AssertAddress(CogBindingTarget target,
        int globalIndex, int sectorIndex, int surfaceIndex)
    {
        Assert.Equal(CogReferenceKind.Surface, target.Kind);
        Assert.Equal(CogBindingTargetStatus.Level, target.Status);
        Assert.Equal(globalIndex, target.Index);
        Assert.Equal(sectorIndex, target.SectorIndex);
        Assert.Equal(surfaceIndex, target.SurfaceIndex);
    }

    // Original two-triangle fixture: the retained global surface table starts
    // with sector 1, while the model's sector traversal starts with sector 0.
    private const string Noncanonical = """
        SECTION: GEORESOURCE
        WORLD COLORMAPS 1
        0: dflt.cmp
        WORLD VERTICES 6
        0: 5 0 0
        1: 6 0 0
        2: 5 1 0
        3: 0 0 0
        4: 1 0 0
        5: 0 1 0
        WORLD TEXTURE VERTICES 1
        0: 0 0
        WORLD ADJOINS 0
        WORLD SURFACES 2
        0: -1 0 0 4 3 3 -1 0 3 0,0 1,0 2,0 1 1 1
        1: -1 0 0 4 3 3 -1 0 3 3,0 4,0 5,0 1 1 1
        0: 0 0 1
        1: 0 0 1
        END
        SECTION: SECTORS
        WORLD SECTORS 2
        SECTOR 0
        VERTICES 3
        0: 3
        1: 4
        2: 5
        SURFACES 1 1
        SECTOR 1
        VERTICES 3
        0: 0
        1: 1
        2: 2
        SURFACES 0 1
        END
        SECTION: COGS
        WORLD COGS 0
        END
        """;
}

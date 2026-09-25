using Sed.Core.Editing;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Cogs;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public class CogBindingsTests
{
    private static Level LevelWithValues(params string[] values)
    {
        var level = new Level();
        level.NewThing(); level.NewThing();
        level.NewSector().NewSurface();
        var second = level.NewSector(); second.NewSurface(); second.NewSurface();
        var cog = new Cog { Name = "fixture.cog" }; cog.Values.AddRange(values);
        level.Cogs.Add(cog); level.Cogs.Add(new Cog { Name = "other.cog" });
        return level;
    }

    private static CogScript Script(string declarations) => CogScript.Parse("fixture.cog", "symbols\n" + declarations + "\nend");

    [Fact]
    public void ReportsReferencesSeparatelyFromPotentialEventLinks()
    {
        var level = LevelWithValues("0", "1", "2", "1", "-1", "0", "1");
        var script = Script("thing a nolink\nthing b linkid=7\nsurface face\ncog other\nthing empty\nsector muted mask=0\nint scalar");
        var description = CogBindings.Describe(level, 0, script);
        Assert.True(description.SchemaComplete);
        Assert.True(description.ParameterCountMatches);
        Assert.Equal(new bool?[] { false, true, true, false, false, false, false }, description.Rows.Select(r => r.EventLinkCandidate));
        Assert.Equal(CogBindingTargetStatus.Level, description.Rows[0].Target.Status);
        Assert.Equal(CogReferenceKind.Thing, description.Rows[0].Target.Kind);
        Assert.Equal(7, description.Rows[1].Symbol!.EffectiveLinkId);
        Assert.Equal(1, description.Rows[2].Target.SectorIndex);
        Assert.Equal(1, description.Rows[2].Target.SurfaceIndex);
        Assert.Equal(CogBindingTargetStatus.Null, description.Rows[4].Target.Status);
        Assert.Equal(CogBindingTargetStatus.NotReference, description.Rows[6].Target.Status);
    }

    [Theory]
    [InlineData("-1", CogBindingTargetStatus.Null)]
    [InlineData("-8", CogBindingTargetStatus.Null)]
    [InlineData("1", CogBindingTargetStatus.Level)]
    [InlineData("2", CogBindingTargetStatus.UnplacedUnchecked)]
    [InlineData("32768", CogBindingTargetStatus.StaticUnchecked)]
    [InlineData("1tail", CogBindingTargetStatus.Invalid)]
    [InlineData("2147483648", CogBindingTargetStatus.Invalid)]
    [InlineData("1.0", CogBindingTargetStatus.Invalid)]
    public void PreservesCogIndexStatusWithoutGuessingRuntimeInstances(string value, CogBindingTargetStatus status)
    {
        var row = Assert.Single(CogBindings.Describe(LevelWithValues(value), 0, Script("cog linked")).Rows);
        Assert.Equal(value, row.RawValue);
        Assert.Equal(status, row.Target.Status);
        Assert.False(row.EventLinkCandidate);
    }

    [Theory]
    [InlineData("thing target", "2")]
    [InlineData("sector target", "2")]
    [InlineData("surface target", "3")]
    public void TargetsUseActualPlacedCounts(string declarations, string index)
    {
        var level = LevelWithValues(index); level.ThingCapacity = 1000;
        var row = Assert.Single(CogBindings.Describe(level, 0, Script(declarations)).Rows);
        Assert.Equal(CogBindingTargetStatus.Invalid, row.Target.Status);
        Assert.False(row.EventLinkCandidate);
    }

    [Fact]
    public void MissingOrAmbiguousSchemaDoesNotLabelRawValuesAsReferences()
    {
        var level = LevelWithValues("0");
        foreach (var script in new CogScript?[] { null, Script("thing a\nfuturetype b"), Script("thing a\nthing A"), CogScript.Parse("wrong.cog", "symbols\nthing a\nend") })
        {
            var description = CogBindings.Describe(level, 0, script);
            Assert.False(description.SchemaComplete);
            Assert.Null(description.ExpectedParameterCount);
            Assert.Null(description.ParameterCountMatches);
            var row = Assert.Single(description.Rows);
            Assert.Null(row.SymbolName);
            Assert.Equal(CogBindingTargetStatus.Unknown, row.Target.Status);
            Assert.Null(row.EventLinkCandidate);
            Assert.Equal("incompleteCogSchema", Assert.Throws<CogBindingException>(() =>
                CogBindings.PrepareRebind(level, 0, 0, "a", CogSymbolType.Thing, 1, script)).Code);
            Assert.Equal("0", Assert.Single(level.Cogs[0].Values));
        }
    }

    [Fact]
    public void CountMismatchDescribesMissingAndExtraRowsButNeverPadsOrDiscards()
    {
        var shortLevel = LevelWithValues("0");
        var script = Script("thing a\nsector b");
        var description = CogBindings.Describe(shortLevel, 0, script);
        Assert.False(description.ParameterCountMatches);
        Assert.Null(description.Rows[1].RawValue);
        Assert.Equal(CogBindingTargetStatus.Missing, description.Rows[1].Target.Status);
        Assert.Equal("cogParameterCountMismatch", Assert.Throws<CogBindingException>(() =>
            CogBindings.PrepareRebind(shortLevel, 0, 1, "b", CogSymbolType.Sector, 1, script)).Code);
        Assert.Single(shortLevel.Cogs[0].Values);

        var longLevel = LevelWithValues("0", "1", "2");
        var extra = CogBindings.Describe(longLevel, 0, script);
        Assert.Equal(CogBindingTargetStatus.Unknown, extra.Rows[2].Target.Status);
        Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(longLevel, 0, 0, "a", CogSymbolType.Thing, 1, script));
        Assert.Equal(3, longLevel.Cogs[0].Values.Count);
    }

    [Fact]
    public void PreparedCommandIsSharedUndoableAndPreservesAllOtherRawValues()
    {
        var level = LevelWithValues(" +000 ", " 1.2500", "texture.mat");
        var script = Script("thing chosen nolink\nflex speed\nmaterial texture");
        var before = level.Cogs[0].Values.ToArray();
        var command = CogBindings.PrepareRebind(level, 0, 0, "chosen", CogSymbolType.Thing, 1, script);
        Assert.IsType<SetCogValueCommand>(command);
        Assert.Equal(before, level.Cogs[0].Values);
        var history = new EditHistory(); history.Do(command);
        Assert.Equal(new[] { "1", " 1.2500", "texture.mat" }, level.Cogs[0].Values);
        history.Undo(); Assert.Equal(before, level.Cogs[0].Values);
        history.Redo(); Assert.Equal("1", level.Cogs[0].Values[0]);
        Assert.Equal("thing chosen nolink", script.LevelValues[0].RawDeclaration);
    }

    [Theory]
    [InlineData("thing", CogSymbolType.Thing, 1)]
    [InlineData("sector", CogSymbolType.Sector, 1)]
    [InlineData("surface", CogSymbolType.Surface, 2)]
    [InlineData("cog", CogSymbolType.Cog, 1)]
    public void SupportedKindsCanRebindOrExplicitlyClear(string typeName, CogSymbolType type, int target)
    {
        var level = LevelWithValues("0"); var script = Script(typeName + " target");
        CogBindings.PrepareRebind(level, 0, 0, "target", type, target, script).Apply();
        Assert.Equal(target.ToString(), level.Cogs[0].Values[0]);
        CogBindings.PrepareRebind(level, 0, 0, "target", type, -1, script).Apply();
        Assert.Equal("-1", level.Cogs[0].Values[0]);
    }

    [Fact]
    public void WrongSymbolTypeOutOfBoundsAndUnsupportedTargetsRefuseWithoutMutation()
    {
        var level = LevelWithValues("0"); var script = Script("thing target");
        Assert.Equal("cogSymbolMismatch", Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(level, 0, 0, "wrong", CogSymbolType.Thing, 1, script)).Code);
        Assert.Equal("cogSymbolMismatch", Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(level, 0, 0, "target", CogSymbolType.Sector, 1, script)).Code);
        Assert.Equal("cogParameterOutOfRange", Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(level, 0, 1, "target", CogSymbolType.Thing, 1, script)).Code);
        foreach (int bad in new[] { -2, 2, int.MaxValue })
            Assert.Equal("invalidCogTarget", Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(level, 0, 0, "target", CogSymbolType.Thing, bad, script)).Code);
        Assert.Equal("0", Assert.Single(level.Cogs[0].Values));
    }

    [Theory]
    [InlineData("2")]
    [InlineData("32768")]
    public void ExistingUnsupportedCogIndexesRefuseRatherThanReinterpret(string value)
    {
        var level = LevelWithValues(value); var script = Script("cog target");
        Assert.Equal("unsupportedCogReference", Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(level, 0, 0, "target", CogSymbolType.Cog, 1, script)).Code);
        Assert.Equal(value, level.Cogs[0].Values[0]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(32768)]
    public void NewRuntimeOrStaticCogTargetsRefuse(int target)
    {
        var level = LevelWithValues("0");
        Assert.Equal("invalidCogTarget", Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(level, 0, 0, "target", CogSymbolType.Cog, target, Script("cog target"))).Code);
    }

    [Fact]
    public void NonReferenceTypesCannotBeReboundAsIndexes()
    {
        var level = LevelWithValues("0");
        Assert.Equal("unsupportedCogBindingType", Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(level, 0, 0, "count", CogSymbolType.Int, 1, Script("int count"))).Code);
    }

    [Fact]
    public void ImportedGlobalSurfaceOrderResolvesCorrectLocalAddressAndSurvivesSave()
    {
        var document = JklParser.ParseDocument(Noncanonical);
        var targets = CogBindings.SurfaceTargets(document);
        Assert.Equal(new int?[] { 0, 1 }, targets.Select(t => t.Index));
        Assert.Equal(new int?[] { 1, 0 }, targets.Select(t => t.SectorIndex));
        Assert.All(targets, t => Assert.Equal(0, t.SurfaceIndex));
        var script = Script("surface target");
        var before = CogBindings.Describe(document, 0, script).Rows[0];
        Assert.Equal(1, before.Target.SectorIndex); Assert.Equal(0, before.Target.SurfaceIndex);
        var command = CogBindings.PrepareRebind(document, 0, 0, "target", CogSymbolType.Surface, 1, script);
        command.Apply();
        var after = CogBindings.Describe(document, 0, script).Rows[0];
        Assert.Equal(0, after.Target.SectorIndex); Assert.Equal(0, after.Target.SurfaceIndex);
        var reopened = JklParser.ParseDocument(JklWriter.Build(document));
        Assert.Equal(0, CogBindings.Describe(reopened, 0, script).Rows[0].Target.SectorIndex);
        command.Revert(); Assert.Equal("0", document.Level.Cogs[0].Values[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedRetainedSurfaceIdentityIsUnknownAndRefusesRebinding(bool changeSource)
    {
        var document = JklParser.ParseDocument(Noncanonical);
        if (changeSource) document.SourceLines[Array.FindIndex(document.SourceLines, l => l.Trim() == "SURFACES 1 1")] = "SURFACES 0 1";
        else document.Level.Sectors.Reverse();
        var script = Script("surface target");
        Assert.Equal(CogBindingTargetStatus.Unknown, CogBindings.Describe(document, 0, script).Rows[0].Target.Status);
        Assert.Equal("unresolvedSurfaceIdentity", Assert.Throws<CogBindingException>(() => CogBindings.PrepareRebind(document, 0, 0, "target", CogSymbolType.Surface, 1, script)).Code);
        Assert.Equal("unresolvedSurfaceIdentity", Assert.Throws<CogBindingException>(() => CogBindings.SurfaceTargets(document)).Code);
        Assert.Equal("0", document.Level.Cogs[0].Values[0]);
    }

    // Original two-triangle model with deliberately reversed sector/global surface order.
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
        WORLD COGS 1
        0: fixture.cog 0
        END
        """;
}

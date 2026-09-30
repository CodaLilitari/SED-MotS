using Sed.Core.Model;
using Sed.Formats.Cogs;
using Xunit;

namespace Sed.Core.Tests;

public sealed class CogThingAppendGuardTests
{
    [Fact]
    public void ExistingPlacedReferencesAndNullPassWithoutChangingAnyRawValues()
    {
        var level = LevelWithCog("0", "+1", "-1", "1");
        level.NewThing();
        var schema = Script("thing first\nthing second\nthing absent\nint scalar");
        var before = level.Cogs[0].Values.ToArray();
        CogThingAppendGuard.Validate(level, _ => schema);
        Assert.Equal(before, level.Cogs[0].Values);
        Assert.Equal(2, level.Things.Count);
        Assert.Single(level.Cogs);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("999")]
    [InlineData("32768")]
    public void NextRemoteFutureAndCogStaticBitValuesAreNotProvenThingReferences(string raw)
    {
        var level = LevelWithCog(raw); level.ThingCapacity = 100_000;
        var error = Assert.Throws<CogBindingException>(() => CogThingAppendGuard.Validate(level, _ => Script("thing future")));
        Assert.Equal("existingThingRuntimeReference", error.Code);
        Assert.Contains("COG 0, parameter 0 ('future')", error.Message);
        Assert.Contains("outside the 1 currently placed things", error.Message);
        Assert.Equal(raw, level.Cogs[0].Values[0]); Assert.Single(level.Things);
    }

    [Theory]
    [InlineData("-2")]
    [InlineData("-01")]
    [InlineData("1.0")]
    [InlineData("0.5")]
    [InlineData("0x0")]
    [InlineData("1suffix")]
    [InlineData("2147483648")]
    [InlineData("")]
    public void NoncanonicalNegativeAndMalformedTypedReferencesAreRefused(string raw)
    {
        var level = LevelWithCog(raw);
        var error = Assert.Throws<CogBindingException>(() => CogThingAppendGuard.Validate(level, _ => Script("thing subject")));
        Assert.Equal("invalidExistingThingReference", error.Code);
        Assert.Equal(raw, level.Cogs[0].Values[0]);
    }

    [Fact]
    public void ScalarValuesAndScriptLocalThingConstantsAreNotGuessedToBeReferences()
    {
        var level = LevelWithCog("1", "32768", "-99", "-1");
        var schema = Script("int next\nflex far\nint negative\nthing absent\nthing computed=999 local\nmessage startup");
        CogThingAppendGuard.Validate(level, _ => schema);
        Assert.Equal(new[] { "1", "32768", "-99", "-1" }, level.Cogs[0].Values);
    }

    [Fact]
    public void NoPlacedCogsNeedsNoResolver()
    {
        var level = new Level();
        CogThingAppendGuard.Validate(level, null);
        CogThingAppendGuard.Validate(level, _ => throw new InvalidOperationException("No lookup was expected."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("symbols\nthing subject\n")]
    [InlineData("symbols\nunknown subject\nend\n")]
    public void MissingOrIncompleteSchemaCannotEstablishSafeAppend(string? text)
    {
        var level = LevelWithCog("0");
        var error = Assert.Throws<CogBindingException>(() => CogThingAppendGuard.Validate(level,
            text is null ? null : _ => CogScript.Parse("guard.cog", text)));
        Assert.Equal("incompleteExistingCogSchema", error.Code);
    }

    [Fact]
    public void ScriptMustMatchTheWholeResourceNameAndItsExactExternalParameterCount()
    {
        var level = LevelWithCog("0");
        Assert.Equal("incompleteExistingCogSchema", Assert.Throws<CogBindingException>(() =>
            CogThingAppendGuard.Validate(level, _ => CogScript.Parse("other/guard.cog", "symbols\nthing subject\nend\n"))).Code);
        Assert.Equal("existingCogParameterCountMismatch", Assert.Throws<CogBindingException>(() =>
            CogThingAppendGuard.Validate(level, _ => Script("thing subject\nint extra"))).Code);
        // Resource lookup spelling is case-insensitive, without basename-only matching.
        CogThingAppendGuard.Validate(level, _ => CogScript.Parse("GUARD.COG", "symbols\nthing subject\nend\n"));
    }

    [Fact]
    public void ASecondValidationRechecksBothSourceSchemaAndCurrentValues()
    {
        var level = LevelWithCog("1"); var schema = Script("int scalar"); int resolutions = 0;
        CogScript? Resolve(string _) { resolutions++; return schema; }
        CogThingAppendGuard.Validate(level, Resolve);
        schema = Script("thing nowTyped");
        Assert.Equal("existingThingRuntimeReference", Assert.Throws<CogBindingException>(() =>
            CogThingAppendGuard.Validate(level, Resolve)).Code);
        level.Cogs[0].Values[0] = "0";
        CogThingAppendGuard.Validate(level, Resolve);
        level.Cogs[0].Values[0] = "10";
        Assert.Equal("existingThingRuntimeReference", Assert.Throws<CogBindingException>(() =>
            CogThingAppendGuard.Validate(level, Resolve)).Code);
        Assert.Equal(4, resolutions);
    }

    [Fact]
    public void CurrentThingCountIsRecheckedInsteadOfRememberingAnEarlierValidReference()
    {
        var level = LevelWithCog("0"); var schema = Script("thing subject");
        CogThingAppendGuard.Validate(level, _ => schema);
        level.Things.Clear();
        Assert.Equal("existingThingRuntimeReference", Assert.Throws<CogBindingException>(() =>
            CogThingAppendGuard.Validate(level, _ => schema)).Code);
    }

    [Fact]
    public void AllPlacedCogsAreInspectedEvenWhenTheyShareTheSameScript()
    {
        var level = LevelWithCog("0"); var last = new Cog { Name = "guard.cog" }; last.Values.Add("1"); level.Cogs.Add(last);
        int calls = 0;
        var error = Assert.Throws<CogBindingException>(() => CogThingAppendGuard.Validate(level, _ => { calls++; return Script("thing subject"); }));
        Assert.Equal("existingThingRuntimeReference", error.Code);
        Assert.Contains("COG 1, parameter 0", error.Message);
        Assert.Equal(1, calls); // Repeated resources are cached only within this validation.
        Assert.Equal(new[] { "0", "1" }, level.Cogs.Select(c => c.Values[0]));
    }

    [Fact]
    public void CogBudgetAcceptsBoundaryAndRefusesBeforeAnyResolverBeyondIt()
    {
        var level = new Level(); var schema = Script("");
        for (int i = 0; i < CogThingAppendGuard.MaxCogs; i++) level.Cogs.Add(new Cog { Name = "guard.cog" });
        CogThingAppendGuard.Validate(level, _ => schema);
        level.Cogs.Add(new Cog { Name = "guard.cog" });
        Assert.Equal("thingAppendCogBudget", Assert.Throws<CogBindingException>(() =>
            CogThingAppendGuard.Validate(level, _ => throw new InvalidOperationException("Budget should refuse before lookup."))).Code);
    }

    [Fact]
    public void ParameterBudgetCountsScalarParametersAndAcceptsItsExactBoundary()
    {
        var level = new Level();
        var schema = Script(string.Join("\n", Enumerable.Range(0, 100).Select(i => "int value" + i)));
        for (int i = 0; i < CogThingAppendGuard.MaxParameters / 100; i++)
        {
            var cog = new Cog { Name = "guard.cog" }; cog.Values.AddRange(Enumerable.Repeat("1", 100)); level.Cogs.Add(cog);
        }
        CogThingAppendGuard.Validate(level, _ => schema);
        level.Cogs[0].Values.Add("1");
        Assert.Equal("thingAppendCogBudget", Assert.Throws<CogBindingException>(() =>
            CogThingAppendGuard.Validate(level, _ => throw new InvalidOperationException("Budget should refuse before lookup."))).Code);
    }

    private static Level LevelWithCog(params string[] values)
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.NewThing();
        var cog = new Cog { Name = "guard.cog" }; cog.Values.AddRange(values); level.Cogs.Add(cog);
        return level;
    }

    private static CogScript Script(string declarations) => CogScript.Parse("guard.cog", "symbols\n" + declarations + "\nend\n");
}

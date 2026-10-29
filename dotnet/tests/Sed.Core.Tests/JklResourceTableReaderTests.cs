using System.Collections;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

/// <summary>Original retained-table fixtures; no game resources or filesystem lookups.</summary>
public sealed class JklResourceTableReaderTests
{
    private static ResourceDependencyReadResult Read(string text, int maxReferences = 16_384) =>
        JklResourceTableReader.Read(text.Replace("\r\n", "\n").Split('\n'), maxReferences);

    [Theory]
    [InlineData("SOUNDS", "sounds", "alarm.WAV", "sound", "jklSound", "alarm.WAV")]
    [InlineData("MODELS", "models", "crate.3do", "3do", "jklModel", "7: crate.3do")]
    [InlineData("SPRITES", "sprites", "flare.spr", "misc/spr", "jklSprite", "7: flare.spr")]
    [InlineData("KEYFRAMES", "keyframes", "walk.key", "3do/key", "jklKeyframe", "7: walk.key")]
    [InlineData("ANIMCLASS", "puppets", "guard.pup", "misc/pup", "jklPuppet", "7: guard.pup")]
    [InlineData("SOUNDCLASS", "soundclasses", "guard.snd", "misc/snd", "jklSoundclass", "7: guard.snd")]
    [InlineData("AICLASS", "aiclasses", "guard.ai", "misc/ai", "jklAiClass", "7: guard.ai")]
    [InlineData("COGSCRIPTS", "scripts", "door.cog", "cog", "jklCogScript", "7: door.cog")]
    public void EachRetainedTableUsesItsVerifiedResourceFolderAndPhysicalLine(
        string section, string noun, string name, string folder, string relation, string row)
    {
        var result = Read($"# original fixture\r\nSection: {section}\r\nWorld {noun} 128\r\n{row}\r\nend\r\n");

        Assert.Equal("jklResourceTables", result.Format);
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal(new ResourceDependency(name, folder, relation, 4), Assert.Single(result.Dependencies));
        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.UnverifiedScopes);
    }

    [Fact]
    public void CapacityIsNotARequiredRowCountAndLabelsDoNotDetermineAllocation()
    {
        var result = Read("SECTION: MODELS\nWorld models 1024\n901: one.3do\n901: two.3do\nend\n" +
            "SECTION: SOUNDS\nWorld sounds 2147483647\nping.wav\nend\n");

        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal(new[] { "one.3do", "two.3do", "ping.wav" }, result.Dependencies.Select(d => d.Name));
        Assert.Equal(new int?[] { 3, 4, 8 }, result.Dependencies.Select(d => d.LineNumber));
    }

    [Fact]
    public void EmptyCapacityAndAbsentOptionalTablesHaveNoRetainedDependencies()
    {
        Assert.Equal(ResourceDependencyReadState.Complete, Read("# no retained resource tables\n").State);
        var result = Read("SECTION: MODELS\nWorld models 0\nend\n", 0);
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Empty(result.Dependencies);
    }

    [Theory]
    [InlineData("COPYRIGHT")]
    [InlineData("HEADER")]
    [InlineData("MATERIALS")]
    [InlineData("GEORESOURCE")]
    [InlineData("SECTORS")]
    [InlineData("THINGS")]
    [InlineData("LIGHTS")]
    [InlineData("LAYERS")]
    [InlineData("COGS")]
    [InlineData("ARCHLIGHTING")]
    [InlineData("SED_SOURCE_LIGHTS")]
    [InlineData("SED_ARCH_AUTHORING")]
    public void ModeledSectionsDoNotProduceGuessedRetainedReferences(string section)
    {
        var result = Read($"SECTION: {section}\n0: do-not-guess.wav\n" +
            "SECTION: SOUNDS\nWorld sounds 1\nactual.wav\nend\n");

        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal("actual.wav", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public void EmptyLegacyJkMarkerIsRecognizedButItsUnmodeledRecordsAreDisclosed()
    {
        var empty = Read("SECTION: JK\n# legacy marker\n; comment only\nSECTION: HEADER\nVersion 1\n");
        Assert.Equal(ResourceDependencyReadState.Complete, empty.State);
        Assert.Empty(empty.UnverifiedScopes);

        var nonempty = Read("SECTION: JK\nnew-meaning unknown.mat\nSECTION: HEADER\nVersion 1\n");
        Assert.Equal(ResourceDependencyReadState.Unsupported, nonempty.State);
        Assert.Empty(nonempty.Dependencies);
        Assert.Contains(nonempty.Diagnostics, d => d.Code == "nonemptyJklMarker" && d.LineNumber == 2);
        Assert.NotEmpty(nonempty.UnverifiedScopes);
    }

    [Fact]
    public void UnknownSectionsDiscloseUnverifiedScopeAndStillRetainOtherKnownTables()
    {
        var result = Read("SECTION: MODELS\nWorld models 1\n0: actual.3do\nend\n" +
            "SECTION: FUTURE_RESOURCES\nWorld materials 1\n0: never-guess.mat\nend\n" +
            "SECTION: SOUNDS\nWorld sounds 1\nactual.wav\nend\n");

        Assert.Equal(ResourceDependencyReadState.Unsupported, result.State);
        Assert.Equal(new[] { "actual.3do", "actual.wav" }, result.Dependencies.Select(d => d.Name));
        Assert.Contains(result.Diagnostics, d => d.Code == "unknownJklSection" && d.LineNumber == 5);
        Assert.Contains(result.UnverifiedScopes, s => s.Contains("FUTURE_RESOURCES", StringComparison.Ordinal));
    }

    [Fact]
    public void CommentsNeverBecomeRowsAndInlineSemicolonsRemainPartOfNames()
    {
        var result = Read("  # SECTION: MODELS\n; discarded.wav\nSECTION: MODELS\n" +
            "# World models 999\nWorld models 1 # capacity\n; 99: wrong.3do\n" +
            "42: semi;colon.3do # do-not-guess.mat\nEND # done\n");

        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal(new ResourceDependency("semi;colon.3do", "3do", "jklModel", 7), Assert.Single(result.Dependencies));
    }

    [Fact]
    public void ConfigArgumentCommasAndValuesDoNotChangeTheSelectedResourceField()
    {
        var result = Read("SECTION: MODELS\nWorld,kind=models,count=2\nindex=27:,file=one.3do\n" +
            "28: two.3do ignored.mat\nend\nSECTION: SOUNDS\nWorld sounds 1\n" +
            "name=alarm.wav preload\nend\n");

        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal(new[] { "one.3do", "two.3do", "alarm.wav" }, result.Dependencies.Select(d => d.Name));
    }

    [Theory]
    [InlineData("SECTION: SOUNDS\nWorld sounds 1\nNONE\nend\n")]
    [InlineData("SECTION: SOUNDCLASS\nWorld soundclasses 1\n0: none\nend\n")]
    public void NullNamesAreOmittedOnlyForVerifiedSoundTables(string text)
    {
        var result = Read(text, 0);
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Empty(result.Dependencies);
    }

    [Fact]
    public void OtherTablesKeepLiteralNoneInsteadOfAssumingAnUnverifiedSentinel()
    {
        var result = Read("SECTION: MODELS\nWorld models 1\n0: none\nend\n");
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal("none", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public void NullRowsStillConsumeDeclaredTableCapacity()
    {
        var result = Read("SECTION: SOUNDS\nWorld sounds 1\nnone\nactual.wav\nend\n", 0);
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, d => d.Code == "resourceTableCapacityExceeded");
    }

    [Fact]
    public void ContinuedTablesDiscloseUnverifiedSyntaxWithoutGuessingPartialNames()
    {
        var result = Read("SECTION: MODELS\nWorld models 1\n0: partly\\\ncontinued.3do\nend\n" +
            "SECTION: SOUNDS\nWorld sounds 1\nactual.wav\nend\n");
        Assert.Equal(ResourceDependencyReadState.Unsupported, result.State);
        Assert.Equal("actual.wav", Assert.Single(result.Dependencies).Name);
        Assert.Contains(result.Diagnostics, d => d.Code == "continuedResourceTable" && d.LineNumber == 3);
        Assert.NotEmpty(result.UnverifiedScopes);
    }

    [Fact]
    public void UnsectionedDataIsDisclosedRatherThanTreatedAsAResourceFreeTable()
    {
        var result = Read("potential-resource.mat\nSECTION: HEADER\nVersion 1\n");
        Assert.Equal(ResourceDependencyReadState.Unsupported, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, d => d.Code == "unsectionedJklData" && d.LineNumber == 1);
        Assert.NotEmpty(result.UnverifiedScopes);
    }

    [Theory]
    [InlineData("crate", "crate")]
    [InlineData("CrAtE", "crate")]
    [InlineData("name=crate", "crate")]
    public void DuplicateRawTemplateNamesCannotHideTheFirstDeclaration(string first, string second)
    {
        var result = Read($"SECTION: TEMPLATES\nWorld templates 64\n{first} none model3d=first.3do\n" +
            $"{second} none model3d=second.3do\nend\n");
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Contains(result.Diagnostics, d => d.Code == "duplicateTemplateName" && d.LineNumber == 4);
    }

    [Fact]
    public void DistinctTemplateNamesRemainValidWithoutCollectingModeledParameterResources()
    {
        var result = Read("SECTION: TEMPLATES\nWorld templates 64\n" +
            "_base none type=actor model3d=already-modeled.3do\n" +
            "crate _base cog=already-modeled.cog\nend\n");
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Empty(result.Dependencies);
    }

    [Fact]
    public void ContinuedTemplatesCannotHideDuplicateNamesBehindAcknowledgedScope()
    {
        var result = Read("SECTION: TEMPLATES\nWorld templates 64\n" +
            "crate none model3d=first.3do\nextra none model3d=other.3do \\\n" +
            "type=actor\nCrAtE none model3d=second.3do\nend\n");
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Contains(result.Diagnostics, d => d.Code == "unsupportedTemplateContinuation" && d.LineNumber == 4);
    }

    [Theory]
    [InlineData("MODELS", "World models 1\n  ; hidden.3do", 3)]
    [InlineData("TEMPLATES", "World templates 2\n  ; none model3d=first.3do\n  ; none model3d=second.3do", 3)]
    [InlineData("HEADER", "World gravity 0\n\t; unknown source record", 3)]
    [InlineData("JK", "  ; not a column-zero comment", 2)]
    public void IndentedSemicolonsAreHardRefusedAsUnsupportedSourceSemantics(string section, string body, int line)
    {
        var result = Read($"SECTION: {section}\n{body}\nend\n");
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, d => d.Code == "unsupportedSemicolonComment" && d.LineNumber == line);
    }

    [Theory]
    [InlineData("TEMPLATES", "World templates 2\ncrate none type=actor,model3d=hidden.3do", "unsupportedModeledComma")]
    [InlineData("THINGS", "World things 1\n0: _actor placed 0 0 0 0 0 0 0 type=actor,model3d=hidden.3do", "unsupportedModeledComma")]
    [InlineData("THINGS", "World things 1\n0: _actor placed 0 0 0 0 0 0 0 \\\nmodel3d=hidden.3do", "unsupportedThingContinuation")]
    [InlineData("TEMPLATES", "World templates 2\n// none type=actor model3d=hidden.3do", "unsupportedTemplateComment")]
    [InlineData("TEMPLATES", "World templates 2\n; counted differently\nfirst none model3d=first.3do\nsecond none model3d=hidden.3do", "unsupportedTemplateComment")]
    public void RawModeledSyntaxCannotHideResourcesThroughDifferentTokenization(string section, string body, string code)
    {
        var result = Read($"SECTION: {section}\n{body}\nend\n");
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.LineNumber == 3);
    }

    [Theory]
    [InlineData("SECTION: MODELS\nend\n", "missingResourceTableHeader")]
    [InlineData("SECTION: MODELS\nWorld models -1\nend\n", "invalidResourceTableHeader")]
    [InlineData("SECTION: MODELS\nWorld models nope\nend\n", "invalidResourceTableHeader")]
    [InlineData("SECTION: MODELS\nWorld models 2147483648\nend\n", "invalidResourceTableHeader")]
    [InlineData("SECTION: MODELS\nWorld models\nend\n", "invalidResourceTableHeader")]
    [InlineData("SECTION: MODELS\nWorld sounds 1\nend\n", "invalidResourceTableHeader")]
    [InlineData("SECTION: MODELS\nWorld models 1 extra\nend\n", "invalidResourceTableHeader")]
    [InlineData("SECTION: MODELS\nWorld models 0\n", "unterminatedResourceTable")]
    [InlineData("SECTION: MODELS\nWorld models 1\n0: actual.3do\n", "unterminatedResourceTable")]
    [InlineData("SECTION: MODELS\nWorld models 0\nSECTION: HEADER\nVersion 1\n", "unterminatedResourceTable")]
    [InlineData("SECTION: MODELS\nWorld models 1\nactual.3do\nend\n", "invalidResourceTableRow")]
    [InlineData("SECTION: MODELS\nWorld models 1\n-1: actual.3do\nend\n", "invalidResourceTableRow")]
    [InlineData("SECTION: MODELS\nWorld models 1\nnope: actual.3do\nend\n", "invalidResourceTableRow")]
    [InlineData("SECTION: MODELS\nWorld models 1\n0:\nend\n", "invalidResourceTableRow")]
    [InlineData("SECTION: MODELS\nWorld models 1\n0:actual.3do\nend\n", "invalidResourceTableRow")]
    [InlineData("SECTION: SOUNDS\nWorld sounds 1\n0: actual.wav\nend\n", "invalidResourceTableRow")]
    [InlineData("SECTION: MODELS\nWorld models 0\nend\n0: ignored.3do\n", "extraResourceTableData")]
    [InlineData("SECTION: MODELS\nWorld models 0\nend\nsection: models\nWorld models 0\nend\n", "duplicateResourceTable")]
    [InlineData("SECTION: MODELS\nWorld wrong -1\n0: partly\\\ncontinued.3do\nend\n", "invalidResourceTableHeader")]
    [InlineData("SECTION: MODELS\nWorld models 1\n0: partly\\\ncontinued.3do\n", "unterminatedResourceTable")]
    [InlineData("SECTION: MODELS\nWorld models 1\n0: partly\\\ncontinued.3do\nend\nextra-data\n", "extraResourceTableData")]
    [InlineData("SECTION: SOUNDS\nWorld sounds 1\nend extra\nend\n", "invalidResourceTableRow")]
    public void MalformedStructureCannotBecomeAnAcknowledgedUnverifiedDependencySet(string text, string code)
    {
        var result = Read(text);
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Contains(result.Diagnostics, d => d.Code == code && d.LineNumber > 0);
    }

    [Theory]
    [InlineData("SECTION: MODELS\nWorld models 0\n0: first.3do\nend\n", 0)]
    [InlineData("SECTION: SOUNDS\nWorld sounds 1\nfirst.wav\nsecond.wav\nend\n", 1)]
    public void RowsBeyondCapacityStopBeforeTheExcessReferenceIsAppended(string text, int retained)
    {
        var result = Read(text);
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, result.State);
        Assert.Equal(retained, result.Dependencies.Count);
        Assert.Contains(result.Diagnostics, d => d.Code == "resourceTableCapacityExceeded");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ReferenceLimitAppliesAcrossSectionsBeforeAppend(int maxReferences)
    {
        var result = Read("SECTION: MODELS\nWorld models 1\n0: one.3do\nend\n" +
            "SECTION: SOUNDS\nWorld sounds 1\ntwo.wav\nend\n", maxReferences);
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, result.State);
        Assert.Equal(maxReferences, result.Dependencies.Count);
        Assert.Contains(result.Diagnostics, d => d.Code == "resourceReferenceLimit");
    }

    [Fact]
    public void OversizedLineIsRejectedBeforeAnyReferenceScanEvenInsideAnIgnoredSection()
    {
        var result = JklResourceTableReader.Read([
            "SECTION: SOUNDS", "World sounds 1", "one.wav", "end",
            "SECTION: COPYRIGHT", new string('x', 16_385),
        ]);
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, d => d.Code == "jklLineLimit" && d.LineNumber == 6);
    }

    [Fact]
    public void ExactLineLengthLimitStillAllowsAnEmptyModeledSection()
    {
        var result = JklResourceTableReader.Read(["SECTION: COPYRIGHT", "#" + new string('x', 16_383)]);
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
    }

    [Fact]
    public void TooManyLinesIsRejectedBeforeIndexingCallerInput()
    {
        var result = JklResourceTableReader.Read(new ExcessLines());
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, d => d.Code == "jklLineCountLimit");
    }

    [Fact]
    public void NegativeCallerReferenceLimitIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => JklResourceTableReader.Read([], -1));
    }

    private sealed class ExcessLines : IReadOnlyList<string>
    {
        public int Count => 1_000_001;
        public string this[int index] => throw new InvalidOperationException("Over-limit input must not be scanned.");
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("Over-limit input must not be scanned.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

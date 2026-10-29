using System.Text;
using System.Reflection;
using Sed.Formats.Cogs;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

/// <summary>Original dependency-bearing text, independent of game files or an installation.</summary>
public sealed class ResourceDependencyReaderTests
{
    private static ResourceDependencyReadResult Read(string folder, string text,
        ResourceDependencyReadLimits? limits = null, string name = "original.resource") =>
        ResourceDependencyReader.Read(name, folder, Encoding.Latin1.GetBytes(text), limits);

    [Fact]
    public void ModelReadsOnlyItsDeclaredMaterialTableWithPhysicalLineProvenance()
    {
        const string text = "# original fixture\r\nSECTION: HEADER\r\n3DO 2.1\r\n" +
            "SECTION: MODELRESOURCE\r\nMATERIALS 2\r\n12: Paint.mat\r\n4: trim.mat # note\r\n" +
            "SECTION: GEOMETRYDEF\r\nNAME misleading.mat\r\n";
        var result = Read("3do", text);

        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal("3do", result.Format);
        Assert.Empty(result.Diagnostics);
        Assert.Collection(result.Dependencies,
            dependency => Assert.Equal(new ResourceDependency("Paint.mat", "mat", "modelMaterial", 6), dependency),
            dependency => Assert.Equal(new ResourceDependency("trim.mat", "mat", "modelMaterial", 7), dependency));
    }

    [Fact]
    public void ZeroMaterialModelIsAnInspectedTableWithoutRequiringGeometry()
    {
        var result = Read("/3DO/", "SECTION: MODELRESOURCE\nMATERIALS 0\nSECTION: GEOMETRYDEF\n");
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Empty(result.Dependencies);
    }

    [Theory]
    [InlineData("SECTION: HEADER\n3DO 2.1\n", "missingModelResource")]
    [InlineData("SECTION: MODELRESOURCE\nMATERIALS -1\n", "invalidMaterialCount")]
    [InlineData("SECTION: MODELRESOURCE\nMATERIALS nope\n", "invalidMaterialCount")]
    [InlineData("SECTION: MODELRESOURCE\nMATERIALS 1\n", "truncatedMaterialTable")]
    [InlineData("SECTION: MODELRESOURCE\nMATERIALS 1\n0:\n", "invalidMaterialRow")]
    [InlineData("SECTION: MODELRESOURCE\nMATERIALS 1\npaint.mat\n", "invalidMaterialRow")]
    [InlineData("SECTION: MODELRESOURCE\nMATERIALS 0\n0: extra.mat\n", "extraMaterialData")]
    [InlineData("SECTION: MODELRESOURCE\nMATERIALS 0\nSECTION: MODELRESOURCE\nMATERIALS 0\n", "duplicateModelResource")]
    public void UnreadableMaterialTablesCannotBeReportedAsDependencyFree(string text, string code)
    {
        var result = Read("3do", text);
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void PuppetReadsAnimationResourcesAndSkipsModeInheritanceAndJoints()
    {
        const string text = "# original puppet\nMODE=0\nstand idle.key\nwalk WALK.KEY 0x0 1 2\n" +
            "mode=1,basedon=0\ncharge charge.key\njoints\n0=1\n1=3\nend\n" +
            "buttpunch punch.key\nblock NONE\n";
        var result = Read("misc\\pup", text);

        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal("pup", result.Format);
        Assert.Equal(new[] { "idle.key", "WALK.KEY", "charge.key", "punch.key" }, result.Dependencies.Select(d => d.Name));
        Assert.Equal(new int?[] { 3, 4, 6, 11 }, result.Dependencies.Select(d => d.LineNumber));
        Assert.All(result.Dependencies, dependency =>
        {
            Assert.Equal("3do/key", dependency.Folder);
            Assert.Equal("puppetKeyframe", dependency.Relation);
        });
    }

    [Fact]
    public void UnknownPuppetRecordIsDisclosedWithoutGuessingAKeyFromItsExtension()
    {
        var result = Read("misc/pup", "stand idle.key\nfuture_mode guessed.key\n");
        Assert.Equal(ResourceDependencyReadState.Unsupported, result.State);
        Assert.Equal("idle.key", Assert.Single(result.Dependencies).Name);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "unknownPuppetRecord" && diagnostic.LineNumber == 2);
    }

    [Theory]
    [InlineData("mode=abc\n", "invalidPuppetMode")]
    [InlineData("mode=1 basedon=\n", "invalidPuppetMode")]
    [InlineData("stand\n", "invalidPuppetAnimation")]
    [InlineData("joints\n0=1\n", "unterminatedPuppetJoints")]
    [InlineData("joints\n0=nope\nend\n", "invalidPuppetJoint")]
    [InlineData("stand idle.key invalid-flags\n", "invalidPuppetAnimation")]
    public void MalformedPuppetRecordsAreIncomplete(string text, string code)
    {
        var result = Read("misc/pup", text);
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void SoundclassRetainsAlternativesAndTreatsNoneAsTheKnownNullSentinel()
    {
        const string text = "; original soundclass\nCREATE first.wav 0x40 0.5 2.5 1\n" +
            "create,second.wav\nidle none\n# misleading.wav\n";
        var result = Read("misc/snd", text);

        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal("snd", result.Format);
        Assert.Collection(result.Dependencies,
            dependency => Assert.Equal(new ResourceDependency("first.wav", "sound", "soundclassSound", 2), dependency),
            dependency => Assert.Equal(new ResourceDependency("second.wav", "sound", "soundclassSound", 3), dependency));
    }

    [Theory]
    [InlineData("create\n", ResourceDependencyReadState.Malformed, "invalidSoundclassRecord")]
    [InlineData("create tone.wav 0 bad\n", ResourceDependencyReadState.Malformed, "invalidSoundclassRecord")]
    [InlineData("future_event guessed.wav\n", ResourceDependencyReadState.Unsupported, "unknownSoundclassEvent")]
    public void UnreadableSoundclassRowsAreNotSilentlySkipped(string text, ResourceDependencyReadState state, string code)
    {
        var result = Read("misc/snd", text);
        Assert.Equal(state, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.LineNumber == 1);
    }

    [Fact]
    public void SpriteUsesOnlyTheFirstDataRecord()
    {
        const string text = "# misleading.mat\n; original sprite\n" +
            "billboard.mat 0 1 2 4 3 1 0 0 0 0\nother.mat 0 1 2 4 3 1 0 0 0 0\n";
        var result = Read("misc/spr", text);
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal(new ResourceDependency("billboard.mat", "mat", "spriteMaterial", 3), Assert.Single(result.Dependencies));
    }

    [Theory]
    [InlineData("# no record\n")]
    [InlineData("billboard.mat 0 1 2\n")]
    [InlineData("billboard.mat 3 1 2 4 3 1 0 0 0 0\n")]
    [InlineData("billboard.mat 0 0 2 4 3 1 0 0 0 0\n")]
    [InlineData("billboard.mat 0 1 NaN 4 3 1 0 0 0 0\n")]
    public void SpriteRequiresAnUnderstoodFirstRecord(string text)
    {
        var result = Read("misc/spr", text);
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "invalidSpriteRecord");
    }

    [Fact]
    public void ParticleMaterialComesFromItsHeaderRatherThanGeometryOrComments()
    {
        const string text = "SECTION: HEADER\nPAR 1.0\nSIZE 0.1\nMATERIAL dust.mat\n" +
            "LIGHTINGMODE 3\nSECTION: GEOMETRYDEF\n# MATERIAL misleading.mat\nMATERIAL unrelated.mat\n";
        var result = Read("misc/par", text);
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal(new ResourceDependency("dust.mat", "mat", "particleMaterial", 4), Assert.Single(result.Dependencies));
    }

    [Theory]
    [InlineData("SECTION: GEOMETRYDEF\nMATERIAL misleading.mat\n", "missingParticleHeader")]
    [InlineData("SECTION: HEADER\nPAR 1.0\nSIZE 1\nLIGHTINGMODE 3\n", "invalidParticleHeader")]
    [InlineData("SECTION: HEADER\nPAR 1.0\nSIZE 1\nMATERIAL\n", "invalidParticleHeader")]
    [InlineData("SECTION: HEADER\nPAR 1.0\nSIZE 1\nMATERIAL a.mat\nMATERIAL b.mat\n", "duplicateParticleMaterial")]
    public void ParticleHeaderFailuresRemainDistinctFromAnEmptyDependencySet(string text, string code)
    {
        var result = Read("misc/par", text);
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public void CogCollectsTypedDefaultsIncludingLocalsWithoutTurningCogIndicesIntoScripts()
    {
        const string text = "symbols\nsound cue=beep.wav local\nmaterial panel=sign.mat\n" +
            "model item=crate.3do local\nkeyframe idle=idle.key local\nai behavior=guard.ai local\n" +
            "cog controller=17 local\ntemplate spawn=crate\nsound absent\nsound silent=none local\n" +
            "end\ncode\nstartup:\nLoadSound(\"dynamic.wav\");\nend\n";
        var result = Read("cog", text, name: "original.cog");

        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal(new[] { "beep.wav", "sign.mat", "crate.3do", "idle.key", "guard.ai" }, result.Dependencies.Select(d => d.Name));
        Assert.Equal(new[] { "sound", "mat", "3do", "3do/key", "misc/ai" }, result.Dependencies.Select(d => d.Folder));
        Assert.Equal(new[] { "cue", "panel", "item", "idle", "behavior" }, result.Dependencies.Select(d => d.SymbolName));
        Assert.Equal(new int?[] { 2, 3, 4, 5, 6 }, result.Dependencies.Select(d => d.LineNumber));
        Assert.All(result.Dependencies, dependency => Assert.Equal("cogDefault", dependency.Relation));
        Assert.Contains(result.UnverifiedScopes, scope => scope.Contains("code", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.UnverifiedScopes, scope => scope.Contains("positional", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.UnverifiedScopes, scope => scope.Contains("template", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("symbols\nsound cue=beep.wav\n", "missingSymbolsEnd")]
    [InlineData("code\nend\n", "missingSymbolsBlock")]
    [InlineData("symbols\nsound cue=beep.wav\nfuturetype mystery=hidden.mat\nend\n", "unknownDeclaration")]
    [InlineData("symbols\nsound cue=beep.wav oddflag\nend\n", "unknownModifier")]
    [InlineData("symbols\nsound cue=one.wav\nsound cue=two.wav\nend\n", "duplicateSymbol")]
    public void IncompleteCogSchemaCannotProduceAssertedTypedDependencyEdges(string text, string code)
    {
        var result = Read("cog", text);
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "cogSchema." + code);
    }

    [Fact]
    public void CogQuotedDescriptionsRemainSupportedButQuotedResourceLiteralsAreDisclosed()
    {
        var valid = Read("cog", "symbols\nsound cue=tone.wav local desc=\"sound # label\"\nend\n");
        Assert.Equal(ResourceDependencyReadState.Complete, valid.State);
        Assert.Equal("tone.wav", Assert.Single(valid.Dependencies).Name);

        var quoted = Read("cog", "symbols\nsound cue=\"tone.wav\" local\nend\n");
        Assert.Equal(ResourceDependencyReadState.Unsupported, quoted.State);
        Assert.Empty(quoted.Dependencies);
        Assert.Contains(quoted.Diagnostics, diagnostic => diagnostic.Code == "unsupportedResourceLiteral" && diagnostic.LineNumber == 2);
    }

    [Fact]
    public void CogSchemaRetainsTheNormalizedLayoutForPlacedParameterInspection()
    {
        var result = Read("cog", "symbols\n; original loader comment\nsound cue\nend\ncode\n# private-code-marker\nend\n");
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        var schema = CompleteCogSchema(result);
        var symbol = Assert.Single(schema.LevelValues);
        Assert.Equal("cue", symbol.Name);
        Assert.Equal(CogSymbolType.Sound, symbol.Type);
        Assert.Equal(3, symbol.LineNumber);
        Assert.DoesNotContain("private-code-marker", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public void UnsupportedDefaultDoesNotEraseOtherwiseCompletePositionalSchema()
    {
        var result = Read("cog", "symbols\nsound fallback=\"unused.wav\" local\nsound required\nend\n");
        Assert.Equal(ResourceDependencyReadState.Unsupported, result.State);
        var schema = CompleteCogSchema(result);
        var symbol = Assert.Single(schema.LevelValues);
        Assert.Equal("required", symbol.Name);
        Assert.Equal(CogSymbolType.Sound, symbol.Type);
    }

    private static CogScript CompleteCogSchema(ResourceDependencyReadResult result)
    {
        // The sibling-format seam is internal so serialized reports cannot leak
        // script source. Exercise its normalized symbols without widening that API.
        var property = typeof(ResourceDependencyReadResult).GetProperty("CogSchema", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        var schema = Assert.IsType<CogScript>(property.GetValue(result));
        Assert.True(schema.IsComplete);
        return schema;
    }

    [Theory]
    [InlineData("mat", "mat")]
    [InlineData("misc/cmp", "cmp")]
    [InlineData("sound", "wav")]
    [InlineData("3do/key", "key")]
    public void DeclaredLeafKindsDoNotInterpretTheirPayloadAsTextOrClaimValidity(string folder, string format)
    {
        var result = ResourceDependencyReader.Read("original", folder, new byte[] { 0, 255, 1 });
        Assert.Equal(ResourceDependencyReadState.Leaf, result.State);
        Assert.Equal(format, result.Format);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.UnverifiedScopes, scope => scope.Contains("validity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AiIsTerminalForThisContractWithDifficultyAndFallbackUncertainty()
    {
        var result = Read("misc/ai", "alignment=0\nlook 1 2\n");
        Assert.Equal(ResourceDependencyReadState.Leaf, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.UnverifiedScopes, scope => scope.Contains("difficulty", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.UnverifiedScopes, scope => scope.Contains("fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FolderSelectsTheGrammarInsteadOfFilenameExtensionGuessing()
    {
        var unknown = Read("misc/new", "stand idle.key\n", name: "pretend.pup");
        Assert.Equal(ResourceDependencyReadState.Unsupported, unknown.State);
        Assert.Empty(unknown.Dependencies);
        Assert.Contains(unknown.Diagnostics, diagnostic => diagnostic.Code == "unsupportedFormat");

        var declared = Read("misc/pup", "stand extensionless\n", name: "original.resource");
        Assert.Equal(ResourceDependencyReadState.Complete, declared.State);
        Assert.Equal("extensionless", Assert.Single(declared.Dependencies).Name);
    }

    [Theory]
    [InlineData("misc/pup", "stand key=idle.key\n", "idle.key", "3do/key")]
    [InlineData("misc/snd", "event=create file=tone.wav\n", "tone.wav", "sound")]
    [InlineData("misc/spr", "material=card.mat 0 1 2 4 3 1 0 0 0 0\n", "card.mat", "mat")]
    public void ArgumentFormatsUseTheValueAfterEqualsLikeTheResourceLoader(string folder, string text, string resource, string targetFolder)
    {
        var result = Read(folder, text);
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        var dependency = Assert.Single(result.Dependencies);
        Assert.Equal(resource, dependency.Name);
        Assert.Equal(targetFolder, dependency.Folder);
    }

    [Fact]
    public void MaterialTableUsesWhitespaceFieldsWithoutRemovingFilenameCommas()
    {
        var result = Read("3do", "SECTION: MODELRESOURCE\nMATERIALS 1\n0: paint,blue.mat\n");
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal("paint,blue.mat", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public void CommentTextDoesNotIntroduceUnsupportedQuotesOrContinuations()
    {
        var result = Read("misc/pup", "stand idle.key # \"not a field\" \\\n");
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal("idle.key", Assert.Single(result.Dependencies).Name);
    }

    [Fact]
    public void Utf8BomDoesNotHideTheFirstResourceRecord()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("stand idle.key\n")).ToArray();
        var result = ResourceDependencyReader.Read("original.pup", "misc/pup", bytes);
        Assert.Equal(ResourceDependencyReadState.Complete, result.State);
        Assert.Equal("idle.key", Assert.Single(result.Dependencies).Name);
    }

    [Theory]
    [InlineData("misc/pup", "stand idle.key \\\nwalk walk.key\n", "unsupportedContinuation")]
    [InlineData("misc/snd", "create \"tone.wav\"\n", "unsupportedQuotedSyntax")]
    [InlineData("misc/snd", "  ; misleading.wav\n", "unsupportedSemicolonComment")]
    public void LexicalBarriersHardRefuseWhenKnownDependencyStructureCannotBeValidated(string folder, string text, string code)
    {
        var result = Read(folder, text);
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.LineNumber == 1);
    }

    [Fact]
    public void ContinuationCannotDowngradeMalformedSupportedMaterialTableToAcknowledgableState()
    {
        var result = Read("3do", "SECTION: MODELRESOURCE\nMATERIALS -1\n0: partly\\\ncontinued.mat\n");
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "unsupportedContinuation" && diagnostic.LineNumber == 3);
    }

    [Fact]
    public void EmbeddedNulCannotHideLaterDependencyBearingText()
    {
        var result = Read("misc/snd", "create tone.wav\0ignored\n");
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "binaryText");
    }

    [Fact]
    public void TextByteBudgetIsCheckedBeforeDecoding()
    {
        var result = Read("misc/pup", "stand idle.key\n", new(MaxTextBytes: 4));
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "textByteLimit");
    }

    [Fact]
    public void PhysicalLineAndLineLengthBudgetsAreDistinct()
    {
        var lines = Read("misc/pup", "# comment\nstand idle.key\nwalk walk.key\n", new(MaxLines: 2));
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, lines.State);
        Assert.Contains(lines.Diagnostics, diagnostic => diagnostic.Code == "lineLimit" && diagnostic.LineNumber == 3);

        var length = Read("misc/pup", "stand idle.key\n", new(MaxLineChars: 8));
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, length.State);
        Assert.Contains(length.Diagnostics, diagnostic => diagnostic.Code == "lineLengthLimit" && diagnostic.LineNumber == 1);
    }

    [Fact]
    public void ReferenceBudgetStopsBeforeTheNextReferenceIsRecorded()
    {
        var result = Read("misc/snd", "create one.wav\ncreate two.wav\n", new(MaxReferences: 1));
        Assert.Equal(ResourceDependencyReadState.LimitExceeded, result.State);
        Assert.Equal("one.wav", Assert.Single(result.Dependencies).Name);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "referenceLimit" && diagnostic.LineNumber == 2);
    }

    [Fact]
    public void OversizedReferenceIsReportedWithoutTruncatingItsName()
    {
        var result = Read("misc/pup", "stand " + new string('x', 1025) + "\n");
        Assert.Equal(ResourceDependencyReadState.Malformed, result.State);
        Assert.Empty(result.Dependencies);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "resourceNameTooLong");
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0)]
    public void InvalidReaderBoundsAreRefused(int bytes, int lines, int chars, int references) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Read("misc/pup", "", new(bytes, lines, chars, references)));
}

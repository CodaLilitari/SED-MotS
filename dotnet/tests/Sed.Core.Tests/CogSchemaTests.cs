using Sed.Formats.Cogs;

namespace Sed.Core.Tests;

public class CogSchemaTests
{
    [Fact]
    public void RetainsOriginalSourceAndOrderedLinkModifiers()
    {
        const string text = "symbols\n  thing door=-1 nolink,linkid=7 mask=0xFFFFFFFF desc=door # note\nend\n";
        var script = CogScript.Parse("original.cog", text);
        Assert.True(script.IsComplete);
        Assert.Equal(text, script.SourceText);
        var symbol = Assert.Single(script.LevelValues);
        Assert.Equal("  thing door=-1 nolink,linkid=7 mask=0xFFFFFFFF desc=door # note", symbol.RawDeclaration);
        Assert.Equal(new[] { "nolink", "linkid=7", "mask=0xFFFFFFFF", "desc=door" }, symbol.RawModifiers);
        Assert.True(symbol.NoLink);
        Assert.Equal(7, symbol.ExplicitLinkId);
        Assert.Equal(7, symbol.EffectiveLinkId);
        Assert.Equal(uint.MaxValue, symbol.ExplicitMask);
        Assert.Equal(uint.MaxValue, symbol.EffectiveMask);
        Assert.Equal(2, symbol.LineNumber);
    }

    [Fact]
    public void ExplicitModifiersAndEngineDefaultsRemainDistinct()
    {
        var script = CogScript.Parse("x.cog", "symbols\nthing a\nthing b linkid=3 nolink mask=0\nend");
        Assert.True(script.IsComplete);
        Assert.Null(script.LevelValues[0].ExplicitLinkId);
        Assert.Null(script.LevelValues[0].ExplicitMask);
        Assert.Equal(0, script.LevelValues[0].EffectiveLinkId);
        Assert.Equal(0x401u, script.LevelValues[0].EffectiveMask);
        Assert.Equal(3, script.LevelValues[1].ExplicitLinkId);
        Assert.Equal(-1, script.LevelValues[1].EffectiveLinkId);
        Assert.Equal(0u, script.LevelValues[1].EffectiveMask);
    }

    [Theory]
    [InlineData("code\nend", "missingSymbolsBlock")]
    [InlineData("symbols\nthing a", "missingSymbolsEnd")]
    [InlineData("symbols\nthing\nend", "unsupportedDeclaration")]
    [InlineData("symbols\nunknown a\nend", "unknownDeclaration")]
    [InlineData("symbols\nthing a b\nend", "unknownModifier")]
    [InlineData("symbols\nthing a,b\nend", "unsupportedDeclaration")]
    [InlineData("symbols\nthing a[2]\nend", "unsupportedDeclaration")]
    [InlineData("symbols\nthing a=\nend", "unsupportedDeclaration")]
    [InlineData("symbols\nthing a linkid=1tail\nend", "invalidModifier")]
    [InlineData("symbols\nthing a mask=0x100000000\nend", "invalidModifier")]
    [InlineData("symbols\nthing a mask=xyz\nend", "invalidModifier")]
    [InlineData("symbols\nthing a newflag=1\nend", "unknownModifier")]
    [InlineData("symbols\nthing a desc=\"unfinished\nend", "unsupportedDeclaration")]
    [InlineData("symbols\nthing a\nTHING A\nend", "duplicateSymbol")]
    [InlineData("symbols\nthing a\nend\nsymbols\nthing b\nend", "multipleSymbolBlocks")]
    public void UnsupportedOrIncompleteLayoutIsDiagnosticRatherThanAnEditableSchema(string text, string code)
    {
        var script = CogScript.Parse("x.cog", text);
        Assert.False(script.IsComplete);
        Assert.Contains(script.Diagnostics, d => d.Code == code);
        Assert.Equal(text, script.SourceText);
    }

    [Fact]
    public void QuotedDefaultsAndCommentsDoNotShiftThePositionalSchema()
    {
        const string text = "symbols\nvector origin='1 2 3' local\nthing a desc=\"a,# // b\" # real comment\nend\ncode\nthing ignored;\nend\n";
        var script = CogScript.Parse("x.cog", text);
        Assert.True(script.IsComplete);
        Assert.Equal(2, script.Symbols.Count);
        Assert.Equal("'1 2 3'", script.Symbols[0].Default);
        Assert.Equal("a", Assert.Single(script.LevelValues).Name);
        Assert.Equal("\"a,# // b\"", script.LevelValues[0].Description);
    }

    [Fact]
    public void UnknownDeclarationsRemainAvailableEvenThoughPermissiveDisplaySkipsThem()
    {
        var script = CogScript.Parse("x.cog", "symbols\nthing a\nfuturetype b metadata=1\nend");
        Assert.Single(script.Symbols);
        Assert.Equal(2, script.RawSymbolDeclarations.Count);
        Assert.Equal("futuretype b metadata=1", script.Diagnostics.Single().RawDeclaration);
        Assert.False(script.IsComplete);
    }
}

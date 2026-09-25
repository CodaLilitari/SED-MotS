using System.Text;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Cogs;
using Sed.Formats.Gob;

namespace Sed.Core.Tests;

public sealed class CogSchemaResolutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-cog-schema-resolution-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _files = new();
    private readonly List<GobArchive> _archives = new();
    private const string SectorScript = "symbols\nsector target\nend\n";
    private const string ThingScript = "symbols\nthing target\nend\n";

    public CogSchemaResolutionTests() => Directory.CreateDirectory(_directory);

    private GobArchive Archive(params (string Name, string Script)[] entries)
    {
        string path = Path.Combine(_directory, _files.Count + ".goo");
        _files.Add(path);
        GobWriter.Build(path, entries.Select(e => (e.Name, Encoding.ASCII.GetBytes(e.Script))));
        var archive = GobArchive.Open(path); _archives.Add(archive); return archive;
    }

    [Fact]
    public void DuplicateStandardEntriesAreUnresolvedAndCannotProvideTypedSchema()
    {
        var library = new CogScriptLibrary(new[] { Archive(("cog/original.cog", SectorScript), ("cog/original.cog", ThingScript)) });
        Assert.Null(library.Get("original.cog"));
        Assert.Null(library.Get("ORIGINAL.COG"));
        var cog = new Cog { Name = "original.cog" }; cog.Values.Add("0");
        Assert.Null(CogValidation.Options(library).ResolveCogParameters!(cog));
    }

    [Fact]
    public void AmbiguousHigherPriorityArchiveNeverFallsThroughToValidLowerArchive()
    {
        var high = Archive(("a/original.cog", SectorScript), ("b/original.cog", ThingScript));
        var low = Archive(("cog/original.cog", SectorScript));
        var library = new CogScriptLibrary(new[] { high, low });
        Assert.Null(library.Get("original.cog"));
        Assert.Null(library.Get("original.cog"));
    }

    [Fact]
    public void StandardFolderWinsOverOtherBasenamesWithinAnArchive()
    {
        var archive = Archive(("alternate/original.cog", ThingScript), ("cog/original.cog", SectorScript));
        var script = new CogScriptLibrary(new[] { archive }).Get("original.cog")!;
        Assert.True(script.IsComplete); Assert.Equal(CogSymbolType.Sector, Assert.Single(script.LevelValues).Type);
    }

    [Theory]
    [InlineData("original.cog", "original.cog")]
    [InlineData("elsewhere/original.cog", "original.cog")]
    [InlineData("elsewhere/original.cog", "elsewhere\\original.cog")]
    public void ExactAndUniqueBasenameFallbackMatchesExplicitProviderPolicy(string entry, string request)
    {
        var library = new CogScriptLibrary(new[] { Archive((entry, SectorScript)) });
        var script = library.Get(request)!;
        Assert.NotNull(script); Assert.Equal(request, script.Name);
        Assert.Equal(CogSymbolType.Sector, Assert.Single(script.LevelValues).Type);
    }

    [Fact]
    public void FirstMatchingArchiveHasPriorityAndMissingHigherArchiveMayFallThrough()
    {
        var missing = Archive(("cog/unrelated.cog", ThingScript));
        var first = Archive(("cog/original.cog", SectorScript));
        var second = Archive(("cog/original.cog", ThingScript));
        var library = new CogScriptLibrary(new[] { missing, first, second });
        Assert.Equal(CogSymbolType.Sector, Assert.Single(library.Get("original.cog")!.LevelValues).Type);
        Assert.Null(library.Get("missing.cog")); Assert.Null(library.Get("missing.cog"));
    }

    [Theory]
    [InlineData("symbols\nfuturetype unseen\nsector target\nend\n")]
    [InlineData("symbols\nsector target\n")]
    [InlineData("symbols\nsector target\nsector TARGET\nend\n")]
    public void IncompleteSchemasNeverSupplyFilteredPositionalTypes(string source)
    {
        var library = new CogScriptLibrary(new[] { Archive(("cog/original.cog", source)) });
        var script = library.Get("original.cog")!;
        Assert.False(script.IsComplete);
        var cog = new Cog { Name = "original.cog" };
        // Match even the permissively parsed count: completeness must still gate it.
        foreach (var symbol in script.LevelValues) cog.Values.Add("999");
        var options = CogValidation.Options(library);
        Assert.Null(options.ResolveCogParameters!(cog));
        var level = new Level(); level.Cogs.Add(cog);
        var issue = Assert.Single(ConsistencyChecker.Check(level, options), i => i.Type == ItemType.Cog);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains("schema unavailable", issue.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ActualCountMismatchRefusesTypedSchema(int count)
    {
        var library = new CogScriptLibrary(new[] { Archive(("cog/original.cog", SectorScript)) });
        var cog = new Cog { Name = "original.cog" };
        for (int i = 0; i < count; i++) cog.Values.Add("999");
        Assert.Null(CogValidation.Options(library).ResolveCogParameters!(cog));
    }

    [Fact]
    public void CompleteExactCountRetainsKindsAndRetailOption()
    {
        const string source = "symbols\nthing a\nsector b\nsurface c\ncog d\nint e\nthing private local\nmessage startup\nend\n";
        var library = new CogScriptLibrary(new[] { Archive(("cog/original.cog", source)) });
        var cog = new Cog { Name = "original.cog" }; cog.Values.AddRange(["0", "0", "0", "0", "0"]);
        var options = CogValidation.Options(library, retailProfile: true);
        Assert.True(options.RetailProfile);
        Assert.Equal(new[] { CogReferenceKind.Thing, CogReferenceKind.Sector, CogReferenceKind.Surface, CogReferenceKind.Cog, CogReferenceKind.Other },
            options.ResolveCogParameters!(cog)!.Select(p => p.Kind));
    }

    public void Dispose()
    {
        foreach (var archive in _archives) archive.Dispose();
        foreach (string file in _files) File.Delete(file);
        Directory.Delete(_directory);
    }
}

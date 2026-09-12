using System.Text;
using Sed.App;
using Sed.Core.Model;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;
using Sed.Formats.ThreeDo;

namespace Sed.Core.Tests;

public sealed class GuiResourceExportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-gui-export-tests-" + Guid.NewGuid().ToString("N"));

    public GuiResourceExportTests() => Directory.CreateDirectory(_directory);

    private string FilePath(string name) => Path.Combine(_directory, name);

    private string Archive(string name, params string[] entries)
    {
        string path = FilePath(name);
        GobWriter.Build(path, entries.Select((entry, index) => (entry, new byte[] { (byte)index })));
        return path;
    }

    private void AssertNoStagingFiles() => Assert.Empty(Directory.EnumerateFiles(_directory, ".sed-export-*", SearchOption.AllDirectories));

    [Fact]
    public void DiskArchiveRecordsExactIdentityAndStreamArchiveCannotClaimIt()
    {
        string path = Archive("source.gob", "3do/column.3do");
        using var disk = GobArchive.Open(path);
        Assert.Equal(Path.GetFullPath(path), disk.SourcePath);
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        using var memory = new GobArchive(stream);
        Assert.Null(memory.SourcePath);
        Assert.Throws<InvalidOperationException>(() => GuiResourceExport.CaptureResources([memory]));
    }

    [Fact]
    public void ResourceSelectionPrefersStandardDirectoryBeforeExactPathOrBasename()
    {
        using var gob = GobArchive.Open(Archive("paths.gob", "alternate/column.3do", "column.3do", "3do/column.3do"));
        Assert.Equal("3do/column.3do", Assert.Single(gob.FindResourceEntries("COLUMN.3DO", "3DO")).NormalizedName);
        Assert.Equal("alternate/column.3do", Assert.Single(gob.FindResourceEntries("alternate\\column.3do", "3do")).NormalizedName);
    }

    [Fact]
    public void ResourceSelectionReturnsAmbiguityWithoutPickingAnArbitraryEntry()
    {
        using var gob = GobArchive.Open(Archive("ambiguous.gob", "first/column.3do", "second/column.3do"));
        Assert.Equal(2, gob.FindResourceEntries("column.3do", "3do").Count);
        Assert.Empty(gob.FindResourceEntries("missing.3do", "3do"));
    }

    [Fact]
    public void ExactResourceNameWinsBeforeBasenameFallback()
    {
        using var gob = GobArchive.Open(Archive("exact.gob", "nested/column.3do", "column.3do"));
        Assert.Equal("column.3do", Assert.Single(gob.FindResourceEntries("column.3do", "3do")).NormalizedName);
    }

    [Fact]
    public void CapturePreservesArchivePrecedenceAndDeduplicatesSamePath()
    {
        string firstPath = Archive("first.gob", "3do/column.3do");
        string secondPath = Archive("second.gob", "3do/column.3do");
        using var first = GobArchive.Open(firstPath);
        using var second = GobArchive.Open(secondPath);
        using var again = GobArchive.Open(firstPath);
        using var resources = GuiResourceExport.CaptureResources([first, second, again]);
        Assert.NotNull(resources);
        Assert.Equal(new[] { firstPath, secondPath }, resources.Archives.Select(a => a.Path));
        Assert.NotNull(resources.Read("column.3do", "3do"));
        Assert.Equal(firstPath, Assert.Single(resources.UsedResources).Archive);
        resources.VerifyUnchanged();
    }

    [Fact]
    public void EmptyArchivesAreExplicitlyUnverified() => Assert.Null(GuiResourceExport.CaptureResources([]));

    [Fact]
    public void AmbiguousEarlierArchiveDoesNotFallThroughToALaterResource()
    {
        using var first = GobArchive.Open(Archive("ambiguous.gob", "first/column.3do", "second/column.3do"));
        string fallback = FilePath("fallback.gob");
        GobWriter.Build(fallback, [("3do/column.3do", Encoding.ASCII.GetBytes(ArchitectureTopologyFixture.Build(new[] { new[] { 3 } })))]);
        using var second = GobArchive.Open(fallback);
        using var resources = GuiResourceExport.CaptureResources([first, second]);
        Assert.Null(resources!.Read("column.3do", "3do"));
        Assert.Empty(resources.UsedResources);
        Assert.Contains(resources.Diagnostics, d => d.Contains("Ambiguous", StringComparison.Ordinal));
        Assert.NotNull(new ModelLibrary(second).Get("column.3do"));
        Assert.Null(new ModelLibrary(first, second).Get("column.3do"));
    }

    [Fact]
    public void SuccessfulBatchPromotesTextAndReadableArchiveAfterStagingEverything()
    {
        string text = FilePath("level.jkl"), archive = FilePath("episode.goo");
        File.WriteAllText(text, "old");
        int checks = 0;
        bool archiveStaged = false;
        GuiResourceExport.Write([
            GuiResourceExport.Text(text, "new level"),
            new GuiExportOutput(archive, temporary =>
            {
                GobWriter.Build(temporary, [("jkl/level.jkl", Encoding.ASCII.GetBytes("new level"))]);
                archiveStaged = true;
            })
        ], () => { Assert.True(archiveStaged); checks++; });
        Assert.Equal(2, checks);
        Assert.Equal("new level", File.ReadAllText(text));
        using (var gob = GobArchive.Open(archive)) Assert.Equal("new level", gob.ReadText(Assert.Single(gob.Entries)));
        AssertNoStagingFiles();
    }

    [Fact]
    public void ProducerFailureLeavesEveryDestinationUntouched()
    {
        string existing = FilePath("existing.jkl"), absent = FilePath("new.goo");
        File.WriteAllText(existing, "original");
        Assert.Throws<IOException>(() => GuiResourceExport.Write([
            GuiResourceExport.Text(existing, "replacement"),
            new GuiExportOutput(absent, temporary => { File.WriteAllText(temporary, "partial"); throw new IOException("fixture failure"); })
        ], () => Assert.Fail("No freshness check should precede completion of staging.")));
        Assert.Equal("original", File.ReadAllText(existing));
        Assert.False(File.Exists(absent));
        AssertNoStagingFiles();
    }

    [Fact]
    public void ChangedResourceAfterStagingPreventsFirstPromotion()
    {
        var document = new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith));
        var resources = new GuardResources();
        string path = FilePath("existing.jkl");
        File.WriteAllText(path, "original");
        var guard = GuiResourceExport.FreshnessGuard(document, ResourceValidatedExport.DocumentRevision(document), resources);
        Assert.Throws<IOException>(() => GuiResourceExport.Write([
            new GuiExportOutput(path, temporary => { File.WriteAllText(temporary, "replacement"); resources.Changed = true; })
        ], guard));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Equal(1, resources.Checks);
        AssertNoStagingFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedInputsBeforeSecondPromotionRestoreFirstDestination(bool firstExisted)
    {
        string first = FilePath("first.jkl"), second = FilePath("second.goo");
        if (firstExisted) File.WriteAllText(first, "original first");
        File.WriteAllText(second, "original second");
        int checks = 0;
        Assert.Throws<IOException>(() => GuiResourceExport.Write([
            GuiResourceExport.Text(first, "replacement first"), GuiResourceExport.Text(second, "replacement second")
        ], () => { if (++checks == 2) throw new IOException("resource changed"); }));
        if (firstExisted) Assert.Equal("original first", File.ReadAllText(first));
        else Assert.False(File.Exists(first));
        Assert.Equal("original second", File.ReadAllText(second));
        AssertNoStagingFiles();
    }

    [Fact]
    public void DuplicateDestinationsAreRejectedBeforeAnyProducerRuns()
    {
        string path = FilePath("same.jkl");
        int writes = 0;
        var output = new GuiExportOutput(path, _ => writes++);
        Assert.Throws<ArgumentException>(() => GuiResourceExport.Write([output, output], () => { }));
        Assert.Equal(0, writes);
        Assert.False(File.Exists(path));
        AssertNoStagingFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DestinationChangedDuringPreparationIsNeverOverwritten(bool existed)
    {
        string path = FilePath("level.jkl");
        if (existed) File.WriteAllText(path, "original");
        Assert.Throws<IOException>(() => GuiResourceExport.Write([GuiResourceExport.Text(path, "replacement")],
            () => File.WriteAllText(path, "external edit")));
        Assert.Equal("external edit", File.ReadAllText(path));
        AssertNoStagingFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RollbackPreservesExternalEditsToAnAlreadyPromotedOutput(bool existed)
    {
        string first = FilePath("first.jkl"), second = FilePath("second.goo");
        if (existed) File.WriteAllText(first, "original");
        int checks = 0;
        var failure = Assert.Throws<AggregateException>(() => GuiResourceExport.Write([
            GuiResourceExport.Text(first, "replacement"), GuiResourceExport.Text(second, "new archive")
        ], () =>
        {
            if (++checks != 2) return;
            File.WriteAllText(first, "external edit");
            throw new IOException("fixture resource changed");
        }));
        Assert.Equal("external edit", File.ReadAllText(first));
        Assert.False(File.Exists(second));
        Assert.Contains("could not be restored", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        if (existed)
            Assert.Equal("original", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(_directory, "*.bak"))));
        else Assert.Empty(Directory.EnumerateFiles(_directory, "*.bak"));
    }

    [Fact]
    public void NativeFingerprintChangeInvalidatesDocumentEvenWhenJklIsUnchanged()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        var thing = new Thing { Level = level, Sector = level.Sectors[0], ArchLightingSourceModelHash = "original" };
        level.Things.Add(thing);
        var document = new JklDocument(level);
        string text = JklWriter.Build(document), revision = ResourceValidatedExport.DocumentRevision(document);
        var guard = GuiResourceExport.FreshnessGuard(document, revision, null);
        thing.ArchLightingSourceModelHash = "changed";
        Assert.Equal(text, JklWriter.Build(document));
        string path = FilePath("level.jkl");
        Assert.Throws<InvalidOperationException>(() => GuiResourceExport.Write([GuiResourceExport.Text(path, text)], guard));
        Assert.False(File.Exists(path));
        AssertNoStagingFiles();
    }

    [Fact]
    public void ByteOutputCapturesItsContentBeforeCallerMutation()
    {
        byte[] content = [1, 2, 3];
        string path = FilePath("snapshot.bin");
        var output = GuiResourceExport.Bytes(path, content);
        content[0] = 99;
        GuiResourceExport.Write([output], () => { });
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    private sealed class GuardResources : IArchitectureResourceProvider
    {
        public bool Changed;
        public int Checks;
        public ResolvedArchitectureModel? ResolveArchitectureModel(string name) => null;
        public void VerifyUnchanged() { Checks++; if (Changed) throw new IOException("fixture resource changed"); }
        public string ResourceRevision => "fixture";
        public IReadOnlyList<ResourceArchiveReceipt> Archives => [];
        public IReadOnlyList<ResourceEntryReceipt> UsedResources => [];
        public IReadOnlyList<string> Diagnostics => [];
    }

    public void Dispose()
    {
        // Only the unique fixture directory created above is ever removed.
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

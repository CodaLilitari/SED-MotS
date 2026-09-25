using System.Security.Cryptography;
using System.Text;
using Sed.Core.Model;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Core.Tests;

public sealed class LevelAssetDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-direct-assets-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _files = new();

    public LevelAssetDiagnosticsTests() => Directory.CreateDirectory(_directory);

    private ResourceArchiveRequest Archive(params (string Name, string Text)[] entries)
    {
        string path = Path.Combine(_directory, _files.Count + ".gob");
        _files.Add(path);
        GobWriter.Build(path, entries.Select(e => (e.Name, Encoding.ASCII.GetBytes(e.Text))));
        return new(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
    }

    [Fact]
    public void ReportsKnownDirectOriginsAndExactCapturedBytesWithoutClaimingRecursiveClosure()
    {
        var level = new Level { MasterCmp = "palette.cmp" };
        var document = new JklDocument(level); document.Materials.Add("stone.mat");
        level.ColorMaps.Add("palette.cmp");
        var sector = level.NewSector(); sector.ColorMap = "palette.cmp"; sector.ColorMapIndex = 0; sector.Sound = "wind.wav";
        sector.NewSurface().Material = "stone.mat";
        var cog = new Cog { Name = "logic.cog" }; cog.Values.Add("do-not-guess.mat"); level.Cogs.Add(cog);
        var template = new Template { Name = "crate", Parent = "none" };
        template.Values["model3d"] = "crate.3do"; template.Values["arbitrary"] = "do-not-guess.wav";
        level.Templates[template.Name] = template;
        level.NewThing().Template = "crate";
        using var resources = new ExplicitArchiveResources([Archive(("mat/stone.mat", "mat"), ("misc/cmp/palette.cmp", "cmp"),
            ("sound/wind.wav", "wav"), ("cog/logic.cog", "cog"), ("3do/crate.3do", "original model bytes"))]);

        var report = LevelAssetDiagnostics.Describe(document, resources);
        Assert.True(report.DirectScanComplete);
        Assert.False(report.RecursiveClosureVerified);
        Assert.Equal(9, report.Rows.Count);
        Assert.All(report.Rows, row => { Assert.Equal("resolved", row.Status); Assert.NotNull(row.Receipt); });
        Assert.Equal(new[] { "materialTable", "colormapTable", "level", "sector", "sector", "surface", "placedCog", "template", "thing" }, report.Rows.Select(r => r.Origin.Kind));
        Assert.Equal("crate", report.Rows[^1].Origin.DeclaredByTemplate);
        Assert.Equal(0, report.Rows[5].Origin.SectorIndex); Assert.Equal(0, report.Rows[5].Origin.SurfaceIndex);
        Assert.Equal(5, resources.ReadRequests.Count);
        Assert.DoesNotContain(resources.ReadRequests, r => r.Name.Contains("do-not-guess", StringComparison.Ordinal));
        var model = report.Rows[^1].Receipt!;
        Assert.Equal("3do\\crate.3do", model.Entry);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes("original model bytes"))).ToLowerInvariant(), model.Sha256);
        Assert.Equal(resources.ResourceRevision, report.ResourceRevision);
        Assert.Contains(report.UnverifiedScopes, s => s.Contains("COG code", StringComparison.Ordinal));
        Assert.Contains(report.UnverifiedScopes, s => s.Contains("Transitive", StringComparison.Ordinal));
        Assert.Contains("not a runtime load", report.ResolutionSemantics);
    }

    [Fact]
    public void FixedKnownFieldsUseTheirVerifiedDirectoriesAndPreserveThingOverrides()
    {
        var level = new Level(); var parent = new Template { Name = "base", Parent = "none" };
        var fields = new[] { ("material", "mat"), ("cog", "cog"), ("soundclass", "misc/snd"), ("puppet", "misc/pup"),
            ("sprite", "misc/spr"), ("particle", "misc/par"), ("model3d", "3do"), ("aiclass", "misc/ai") };
        foreach (var field in fields)
        {
            Assert.NotEqual(TemplateParamKind.Text, TemplateParams.KindOf(field.Item1));
            parent.Values[field.Item1] = field.Item1 + ".resource";
        }
        level.Templates[parent.Name] = parent;
        var child = new Template { Name = "child", Parent = "base" }; level.Templates[child.Name] = child;
        var thing = level.NewThing(); thing.Template = "child"; thing.Values["model3d"] = "override.3do";
        using var resources = new ExplicitArchiveResources([Archive(fields.Select(f => (f.Item2 + "/" + f.Item1 + ".resource", f.Item1)).Append(("3do/override.3do", "override")).ToArray())]);
        var report = LevelAssetDiagnostics.Describe(new JklDocument(level), resources);
        var rows = report.Rows.Where(r => r.Origin.Kind == "thing").ToArray();
        Assert.Equal(8, rows.Length);
        foreach (var field in fields)
        {
            var row = Assert.Single(rows, r => r.Origin.Field == field.Item1);
            Assert.Equal(field.Item2, row.Folder); Assert.Equal("resolved", row.Status);
            Assert.Equal(field.Item1 == "model3d" ? null : "base", row.Origin.DeclaredByTemplate);
        }
        Assert.Equal("override.3do", Assert.Single(rows, r => r.Origin.Field == "model3d").Name);
    }

    [Fact]
    public void MissingAmbiguousAndUnverifiedRemainDifferentAndDoNotFallbackAcrossAmbiguity()
    {
        var document = new JklDocument(new Level()); document.Materials.AddRange(["missing.mat", "duplicate.mat"]);
        using var resources = new ExplicitArchiveResources([Archive(("a/duplicate.mat", "a"), ("b/duplicate.mat", "b")), Archive(("mat/duplicate.mat", "lower"))]);
        var report = LevelAssetDiagnostics.Describe(document, resources);
        Assert.Equal("missing", report.Rows[0].Status);
        Assert.Equal("unknown", report.Rows[1].Status);
        Assert.Contains("ambiguous", report.Rows[1].Detail);
        Assert.All(report.Rows, r => Assert.Null(r.Receipt));
        Assert.Equal(ResourceLookupStatus.Missing, resources.StatusFor("missing.mat", "mat"));
        Assert.Equal(ResourceLookupStatus.Ambiguous, resources.StatusFor("duplicate.mat", "mat"));
        Assert.Empty(resources.UsedResources);
        Assert.Equal(2, resources.ReadRequests.Count);
        var unverified = LevelAssetDiagnostics.Describe(document, null);
        Assert.All(unverified.Rows, r => { Assert.Equal("unknown", r.Status); Assert.Contains("No explicit", r.Detail); });
        Assert.Null(unverified.ResourceRevision); Assert.Empty(unverified.Archives);
    }

    [Fact]
    public void CachedReceiptsStayBoundToFolderWhenRequestedNamesAreIdentical()
    {
        using var resources = new ExplicitArchiveResources([Archive(("mat/shared", "mat"), ("3do/shared", "3do"))]);
        resources.Read("shared", "3do"); resources.Read("shared", "mat");
        var level = new Level(); var thing = level.NewThing(); thing.Values["material"] = "shared"; thing.Values["model3d"] = "shared";
        var report = LevelAssetDiagnostics.Describe(new JklDocument(level), resources);
        Assert.Equal("mat\\shared", Assert.Single(report.Rows, r => r.Folder == "mat").Receipt!.Entry);
        Assert.Equal("3do\\shared", Assert.Single(report.Rows, r => r.Folder == "3do").Receipt!.Entry);
        Assert.Equal("mat\\shared", resources.ReceiptFor("SHARED", "/MAT/")!.Entry);
        Assert.Equal(2, resources.ReadRequests.Count);
    }

    [Fact]
    public void MissingTemplateAndCyclesAreBoundedUnknownRelationsButDirectFieldsAreStillReported()
    {
        var level = new Level();
        var one = new Template { Name = "one", Parent = "two" }; one.Values["model3d"] = "local.3do";
        var two = new Template { Name = "two", Parent = "one" };
        level.Templates["one"] = one; level.Templates["two"] = two;
        level.NewThing().Template = "missing";
        var report = LevelAssetDiagnostics.Describe(new JklDocument(level), null);
        Assert.True(report.DirectScanComplete);
        Assert.True(report.WorkUsed < 200);
        Assert.Equal(3, report.Rows.Count(r => r.Origin.Field == "template"));
        Assert.Contains(report.Rows, r => r.Detail?.Contains("cycle", StringComparison.Ordinal) == true);
        Assert.Contains(report.Rows, r => r.Name == "missing" && r.Detail?.Contains("not in the current level", StringComparison.Ordinal) == true);
        Assert.Equal(2, report.Rows.Count(r => r.Name == "local.3do"));
    }

    [Fact]
    public void EmptyExplicitKnownFieldIsUnknownAndNeverGuessedFromParent()
    {
        var level = new Level(); var template = new Template { Name = "parent" }; template.Values["model3d"] = "parent.3do"; level.Templates[template.Name] = template;
        var thing = level.NewThing(); thing.Template = template.Name; thing.Values["model3d"] = "";
        var row = Assert.Single(LevelAssetDiagnostics.Describe(new JklDocument(level), null).Rows, r => r.Origin.Kind == "thing");
        Assert.Equal("", row.Name); Assert.Equal("unknown", row.Status); Assert.Contains("empty", row.Detail);
    }

    [Fact]
    public void OnlyKnownSoundSentinelsAvoidResourceLookup()
    {
        var level = new Level(); level.NewSector().Sound = "none";
        var thing = level.NewThing(); thing.Values["soundclass"] = "none"; thing.Values["model3d"] = "none";
        using var resources = new ExplicitArchiveResources([Archive(("unrelated", "x"))]);
        var report = LevelAssetDiagnostics.Describe(new JklDocument(level), resources);
        Assert.Equal(2, report.Rows.Count(r => r.Status == "null"));
        Assert.Equal("missing", Assert.Single(report.Rows, r => r.Origin.Field == "model3d").Status);
        Assert.Single(resources.ReadRequests); Assert.Equal("3do", resources.ReadRequests[0].Folder);
    }

    [Fact]
    public void RowBudgetStopsBeforeAnyFurtherResourceRead()
    {
        var document = new JklDocument(new Level()); document.Materials.AddRange(["one", "two", "three"]);
        using var resources = new ExplicitArchiveResources([Archive(("mat/one", "one"), ("mat/two", "two"), ("mat/three", "three"))]);
        var report = LevelAssetDiagnostics.Describe(document, resources, maxRows: 2);
        Assert.False(report.DirectScanComplete); Assert.Equal(2, report.Rows.Count);
        Assert.Equal(2, resources.ReadRequests.Count); Assert.Contains(report.Diagnostics, d => d.Contains("row limit", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkBudgetBoundsEmptyCollectionsAndAncestryTraversal()
    {
        var level = new Level(); for (int i = 0; i < 100; i++) level.NewSector();
        var report = LevelAssetDiagnostics.Describe(new JklDocument(level), null, maxWork: 5);
        Assert.False(report.DirectScanComplete); Assert.Equal(5, report.WorkUsed); Assert.Empty(report.Rows);
        Assert.Contains(report.Diagnostics, d => d.Contains("work limit", StringComparison.Ordinal));
        var cyclic = new Level(); cyclic.Templates["self"] = new Template { Name = "self", Parent = "self" };
        var ancestry = LevelAssetDiagnostics.Describe(new JklDocument(cyclic), null, maxWork: 3);
        Assert.False(ancestry.DirectScanComplete); Assert.Equal(3, ancestry.WorkUsed);
    }

    [Fact]
    public void ReceiptSnapshotsStayStableAfterFurtherProviderReads()
    {
        using var resources = new ExplicitArchiveResources([Archive(("mat/one", "one"), ("mat/two", "two"))]);
        var document = new JklDocument(new Level()); document.Materials.Add("one");
        var report = LevelAssetDiagnostics.Describe(document, resources);
        string revision = report.ResourceRevision!;
        resources.Read("two", "mat");
        Assert.Single(report.Rows); Assert.Equal(revision, report.ResourceRevision); Assert.NotEqual(resources.ResourceRevision, report.ResourceRevision);
    }

    [Fact]
    public void AncestorDepthBoundDoesNotInventFieldsFromBeyondTheSupportedModelChain()
    {
        var level = new Level();
        // Put the child first so its report directly exercises the depth guard.
        for (int i = 0; i < 34; i++) level.Templates["t" + i] = new Template { Name = "t" + i, Parent = i == 33 ? "none" : "t" + (i + 1) };
        level.Templates["t33"].Values["model3d"] = "distant.3do";
        var report = LevelAssetDiagnostics.Describe(new JklDocument(level), null);
        var childRows = report.Rows.Where(r => r.Origin.TemplateName == "t0").ToArray();
        var row = Assert.Single(childRows);
        Assert.Equal("template", row.Origin.Field); Assert.Equal("unknown", row.Status);
        Assert.Contains("32-ancestor", row.Detail);
    }

    [Fact]
    public void UnresolvedNumericSlotsAndOversizedNamesNeverCauseGuessedReads()
    {
        var level = new Level(); var sector = level.NewSector(); sector.ColorMapIndex = 7;
        sector.NewSurface().MaterialIndex = 9;
        var thing = level.NewThing(); thing.Values["model3d"] = new string('x', 1025);
        using var resources = new ExplicitArchiveResources([Archive(("misc/cmp/dflt.cmp", "palette"), ("mat/dflt.mat", "material"))]);
        var report = LevelAssetDiagnostics.Describe(new JklDocument(level), resources);
        Assert.Equal(3, report.Rows.Count); Assert.All(report.Rows, r => Assert.Equal("unknown", r.Status));
        Assert.Empty(resources.ReadRequests);
    }

    [Theory]
    [InlineData(0, 10)] [InlineData(100001, 10)] [InlineData(10, 0)] [InlineData(10, 1000001)]
    public void InvalidBoundsRefuse(int rows, int work) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        LevelAssetDiagnostics.Describe(new JklDocument(new Level()), null, rows, work));

    public void Dispose()
    {
        foreach (string file in _files) File.Delete(file);
        Directory.Delete(_directory);
    }
}

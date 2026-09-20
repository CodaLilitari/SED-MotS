using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Core.Tests;

public sealed class JklWriterTransactionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-writer-transaction-" + Guid.NewGuid().ToString("N"));
    public JklWriterTransactionTests() => Directory.CreateDirectory(_directory);

    // Original synthetic tables intentionally disagree with sector traversal
    // order. There is no source-light extension to supply an incidental guard.
    private const string RawNoncanonical = """
        SECTION: HEADER
        VERSION 1
        END
        SECTION: GEORESOURCE
        WORLD COLORMAPS 0
        WORLD VERTICES 6
        0: 50 0 0
        1: 51 0 0
        2: 50 1 0
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
        0: synthetic-reference-witness.cog 1 0
        END
        """;

    [Fact]
    public void RawUnparsedGeometryCannotBeReplacedThroughSourceLinesConstructor()
    {
        var parsed = JklParser.ParseDocument(RawNoncanonical);
        Assert.Equal(3, parsed.Level.Sectors[0].Vertices[0].SourceIndex);
        Assert.Equal(0, parsed.Level.Sectors[1].Vertices[0].SourceIndex);
        Assert.Equal(new[] { "1", "0" }, Assert.Single(parsed.Level.Cogs).Values);
        Assert.DoesNotContain("SED_SOURCE_LIGHTS", RawNoncanonical);

        string path = Path.Combine(_directory, "original.jkl");
        File.WriteAllText(path, RawNoncanonical);
        byte[] original = File.ReadAllBytes(path);
        var unparsed = new JklDocument(File.ReadAllLines(path));
        new CreateSectorCommand(unparsed.Level, SectorFactory.CreateBox(unparsed.Level, new(10, 0, 0), 1, string.Empty, -1)).Apply();
        Assert.Throws<InvalidOperationException>(() => JklWriter.Build(unparsed));
        Assert.Throws<InvalidOperationException>(() => JklWriter.Save(unparsed, path));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void SourceLessModelDocumentStillBuildsAndSaves()
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        var document = new JklDocument(level);
        new CreateSectorCommand(level, SectorFactory.CreateBox(level, new(3, 0, 0), 1, string.Empty, -1)).Apply();
        string expected = JklWriter.Build(document);
        string path = Path.Combine(_directory, "new-model.jkl");
        JklWriter.Save(document, path);
        Assert.Equal(expected, File.ReadAllText(path));
        var reopened = JklParser.ParseDocument(File.ReadAllText(path));
        Assert.Equal(2, reopened.Level.Sectors.Count);
        Assert.Equal(ProjectType.MysteriesOfTheSith, reopened.Level.Kind);
    }

    [Fact]
    public void FinalMoveFailureRetainsDestinationAndMaterialStateThenRetriesLikeCleanSave()
    {
        string source = JklWriter.Build(new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith)));
        var document = JklParser.ParseDocument(source);
        document.Level.Sectors[0].Surfaces[0].Material = "synthetic-new.mat";
        string[] materialsBefore = document.Materials.ToArray();
        Assert.DoesNotContain("synthetic-new.mat", materialsBefore);
        var clean = JklParser.ParseDocument(source);
        clean.Level.Sectors[0].Surfaces[0].Material = "synthetic-new.mat";
        string expected = JklWriter.Build(clean);
        Assert.Contains("synthetic-new.mat", expected);
        Assert.Equal(materialsBefore, clean.Materials); // Successful Build is a preview, not a commit.

        // Content generation is valid and the parent is writable. The final
        // destination is an existing directory, so promotion must fail after
        // the same-parent temporary-file write in Save's current implementation.
        string blocked = Path.Combine(_directory, "blocked.jkl");
        Directory.CreateDirectory(blocked);
        string marker = Path.Combine(blocked, "keep.txt");
        File.WriteAllText(marker, "retain destination marker");
        string writeProbe = Path.Combine(_directory, "parent-write-probe.tmp");
        File.WriteAllText(writeProbe, expected);
        Assert.Equal(expected, File.ReadAllText(writeProbe));
        File.Delete(writeProbe);
        var error = Record.Exception(() => JklWriter.Save(document, blocked));
        Assert.True(error is IOException or UnauthorizedAccessException, $"Expected final-move filesystem failure, got {error?.GetType().Name ?? "success"}.");
        Assert.True(Directory.Exists(blocked));
        Assert.Equal("retain destination marker", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.Equal(materialsBefore, document.Materials);

        string retry = Path.Combine(_directory, "retry.jkl");
        JklWriter.Save(document, retry);
        Assert.Equal(expected, File.ReadAllText(retry));
        Assert.Equal(JklParser.ParseDocument(expected).Materials, document.Materials);
        Assert.Equal("retain destination marker", File.ReadAllText(marker));
        Assert.Equal(new[] { retry }, Directory.GetFiles(_directory));
        Assert.Equal(expected, JklWriter.Build(JklParser.ParseDocument(File.ReadAllText(retry))));
    }

    [Theory]
    [InlineData("opaque-section")]
    [InlineData("unmodeled-header")]
    public void RefusedGenerationDoesNotChangeCachedNormalsOrCornerState(string refusal)
    {
        string source = JklWriter.Build(new JklDocument(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith)));
        source = refusal switch
        {
            "opaque-section" => source + "\nSECTION: UNKNOWN_TOPOLOGY\n0 1 2\nEND\n",
            "unmodeled-header" => source.Replace("SECTION: HEADER", "SECTION: HEADER\nUNKNOWN HEADER FIELD 7", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal)),
        };
        var document = JklParser.ParseDocument(source);
        var surface = document.Level.Sectors[0].Surfaces[0];
        Vec3 cachedNormal = surface.Normal;
        Assert.NotEqual(Vec3.Zero, cachedNormal);
        // Normal has a private setter. Reverse the polygon without invoking
        // RecalcNormal to produce an intentional stale cache through public API.
        surface.Corners.Reverse();
        var independent = new Surface(surface.Sector);
        independent.Corners.AddRange(surface.Corners);
        independent.RecalcNormal();
        Assert.Equal(cachedNormal * -1, independent.Normal);
        if (refusal == "unmodeled-header") document.Level.Header.Gravity += 1;
        var corners = surface.Corners.ToArray();
        var positions = document.Level.Sectors.SelectMany(sector => sector.Vertices).Select(vertex => vertex.Position).ToArray();
        var normals = document.Level.Sectors.SelectMany(sector => sector.Surfaces).Select(face => face.Normal).ToArray();
        string[] materials = document.Materials.ToArray();
        string[] retainedSource = document.SourceLines.ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains(refusal == "opaque-section" ? "UNKNOWN_TOPOLOGY" : "HEADER", error.Message);
        Assert.Equal(cachedNormal, surface.Normal);
        Assert.Equal(normals, document.Level.Sectors.SelectMany(sector => sector.Surfaces).Select(face => face.Normal));
        Assert.Equal(corners, surface.Corners);
        Assert.Equal(positions, document.Level.Sectors.SelectMany(sector => sector.Vertices).Select(vertex => vertex.Position));
        Assert.Equal(materials, document.Materials);
        Assert.Equal(retainedSource, document.SourceLines);
    }

    [Theory]
    [InlineData("cog-argument")]
    [InlineData("material-row")]
    public void ChangedRetainedSourceOutsideGeometryRequiresReopeningBeforeAnUnrelatedEdit(string mutation)
    {
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        var seed = new JklDocument(level);
        seed.Materials.Add("original-retained.mat");
        level.Sectors[0].Surfaces[0].Material = seed.Materials[0];
        level.Sectors[0].Surfaces[0].MaterialIndex = 0;
        var cog = new Cog { Name = "synthetic-retained.cog" };
        cog.Values.Add("0");
        level.Cogs.Add(cog);
        var document = JklParser.ParseDocument(JklWriter.Build(seed));
        if (mutation == "cog-argument")
        {
            int line = Array.FindIndex(document.SourceLines, value => value.Contains("synthetic-retained.cog", StringComparison.Ordinal));
            Assert.True(line >= 0);
            Assert.EndsWith("\t0", document.SourceLines[line]);
            document.SourceLines[line] = document.SourceLines[line][..^1] + "1";
        }
        else
        {
            int line = Array.FindIndex(document.SourceLines, value => value.StartsWith("0: original-retained.mat", StringComparison.Ordinal));
            Assert.True(line >= 0);
            document.SourceLines[line] = document.SourceLines[line].Replace("original-retained.mat", "changed-retained.mat", StringComparison.Ordinal);
        }
        document.Level.Header.Gravity += 1;
        float intendedGravity = document.Level.Header.Gravity;
        string changedSource = string.Join('\n', document.SourceLines);
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reopen", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(changedSource, string.Join('\n', document.SourceLines));
        Assert.Equal("0", document.Level.Cogs[0].Values[0]);
        Assert.Equal("original-retained.mat", document.Materials[0]);

        var reopened = JklParser.ParseDocument(changedSource);
        reopened.Level.Header.Gravity = intendedGravity;
        var saved = JklParser.ParseDocument(JklWriter.Build(reopened));
        Assert.Equal(intendedGravity, saved.Level.Header.Gravity);
        Assert.Equal(mutation == "cog-argument" ? "1" : "0", saved.Level.Cogs[0].Values[0]);
        Assert.Equal(mutation == "material-row" ? "changed-retained.mat" : "original-retained.mat", saved.Materials[0]);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

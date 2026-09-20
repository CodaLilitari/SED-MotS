using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Jkl;
using Xunit;

namespace Sed.Core.Tests;

public class JklExportReferenceTests
{
    // Surface and vertex tables deliberately do not follow sector traversal order.
    // A canonicalizing writer must not silently change the referents of these rows.
    private const string Noncanonical = """
        SECTION: HEADER
        VERSION 1
        END
        SECTION: GEORESOURCE
        WORLD COLORMAPS 1
        0: dflt.cmp
        WORLD VERTICES 6
        0: 10 0 0
        1: 11 0 0
        2: 10 1 0
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
        0: original-synthetic.cog 0 1
        END
        """;

    [Fact]
    public void SurfaceEditPreservesNoncanonicalGlobalSurfaceAndVertexReferents()
    {
        var document = JklParser.ParseDocument(Noncanonical);
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .375f;

        var output = JklWriter.Build(document);
        var reopened = JklParser.ParseDocument(output);

        Assert.Equal(new Vec3(0, 0, 0), reopened.Level.Sectors[0].Vertices[0].Position);
        Assert.Equal(new Vec3(10, 0, 0), reopened.Level.Sectors[1].Vertices[0].Position);
        Assert.Equal(.375f, reopened.Level.Sectors[0].Surfaces[0].ExtraLightIntensity);
        Assert.Equal(0f, reopened.Level.Sectors[1].Surfaces[0].ExtraLightIntensity);
        Assert.Equal(new[] { "0", "1" }, reopened.Level.Cogs[0].Values);
        Assert.Contains("SURFACES 1 1", output);
        Assert.Contains("SURFACES 0 1", output);
        Assert.Equal(3, reopened.Level.Sectors[0].Vertices[0].SourceIndex);
        Assert.Equal(0, reopened.Level.Sectors[1].Vertices[0].SourceIndex);
        Assert.Equal(output, JklWriter.Build(reopened));
    }

    [Fact]
    public void AppendedSectorKeepsOriginalGlobalRowsWhenSectorSectionAlsoChanges()
    {
        var document = JklParser.ParseDocument(Noncanonical);
        document.Level.Sectors.Add(SectorFactory.CreateBox(document.Level, new Vec3(20, 0, 0), 1, "", -1));
        var output = JklWriter.Build(document);
        var reopened = JklParser.ParseDocument(output);

        Assert.Equal(3, reopened.Level.Sectors.Count);
        Assert.Equal(new Vec3(0, 0, 0), reopened.Level.Sectors[0].Vertices[0].Position);
        Assert.Equal(new Vec3(10, 0, 0), reopened.Level.Sectors[1].Vertices[0].Position);
        Assert.Equal(new[] { "0", "1" }, reopened.Level.Cogs[0].Values);
        var surfaceRows = TableRows(output, "World surfaces");
        Assert.Equal("0,0", surfaceRows[0][10]);
        Assert.Equal("3,0", surfaceRows[1][10]);
        Assert.Equal(output, JklWriter.Build(reopened));
        reopened.Level.Sectors[2].Surfaces[0].ExtraLightIntensity = .0625f;
        reopened.Level.Sectors.Add(SectorFactory.CreateBox(reopened.Level, new Vec3(30, 0, 0), 1, "", -1));
        var secondOutput = JklWriter.Build(reopened);
        var second = JklParser.ParseDocument(secondOutput);
        Assert.Equal(.0625f, second.Level.Sectors[2].Surfaces[0].ExtraLightIntensity);
        Assert.Equal(new Vec3(19, -1, -1), second.Level.Sectors[2].Vertices[0].Position);
        Assert.Equal(6, second.Level.Sectors[2].Vertices[0].SourceIndex);
        Assert.Equal("0.0625", TableRows(secondOutput, "World surfaces")[2][8]);
        Assert.Equal(secondOutput, JklWriter.Build(second));
    }

    [Fact]
    public void NoncanonicalAdjoinSlotsAndMirrorsSurviveLightEditAndAppendedPair()
    {
        var text = Noncanonical.Replace("WORLD ADJOINS 0", "WORLD ADJOINS 2\n0: 0x3 1 0\n1: 0x7 0 0")
            .Replace("0: -1 0 0 4 3 3 -1", "0: -1 0 0 4 3 3 1")
            .Replace("1: -1 0 0 4 3 3 -1", "1: -1 0 0 4 3 3 0");
        var document = JklParser.ParseDocument(text);
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .5f;
        var a = SectorFactory.CreateBox(document.Level, new Vec3(20, 0, 0), 1, "", -1);
        var b = SectorFactory.CreateBox(document.Level, new Vec3(22, 0, 0), 1, "", -1);
        document.Level.Sectors.AddRange([a, b]);
        a.Surfaces[5].Adjoin = b.Surfaces[4]; b.Surfaces[4].Adjoin = a.Surfaces[5];
        a.Surfaces[5].AdjoinFlags = b.Surfaces[4].AdjoinFlags = 3;
        var output = JklWriter.Build(document);
        var reopened = JklParser.ParseDocument(output);

        var surfaces = TableRows(output, "World surfaces");
        Assert.Equal("1", surfaces[0][7]); Assert.Equal("0", surfaces[1][7]);
        var adjoins = TableRows(output, "World adjoins");
        Assert.Equal(4, adjoins.Count);
        Assert.Equal("1", adjoins[0][2]); Assert.Equal("0", adjoins[1][2]);
        Assert.Equal("3", adjoins[2][2]); Assert.Equal("2", adjoins[3][2]);
        Assert.Equal("0x3", adjoins[0][1]); Assert.Equal("0x7", adjoins[1][1]);
        Assert.Equal("0x3", adjoins[2][1]); Assert.Equal("0x3", adjoins[3][1]);
        Assert.Equal(3L, reopened.Level.Sectors[0].Surfaces[0].AdjoinFlags);
        Assert.Equal(7L, reopened.Level.Sectors[1].Surfaces[0].AdjoinFlags);
        Assert.Equal(3L, reopened.Level.Sectors[2].Surfaces[5].AdjoinFlags);
        Assert.Equal(3L, reopened.Level.Sectors[3].Surfaces[4].AdjoinFlags);
        Assert.Same(reopened.Level.Sectors[1].Surfaces[0], reopened.Level.Sectors[0].Surfaces[0].Adjoin);
        Assert.Same(reopened.Level.Sectors[0].Surfaces[0], reopened.Level.Sectors[1].Surfaces[0].Adjoin);
        Assert.Same(reopened.Level.Sectors[3].Surfaces[4], reopened.Level.Sectors[2].Surfaces[5].Adjoin);
        Assert.Same(reopened.Level.Sectors[2].Surfaces[5], reopened.Level.Sectors[3].Surfaces[4].Adjoin);
        Assert.Equal(output, JklWriter.Build(reopened));
    }

    [Fact]
    public void OriginalSectorReorderingRefusesBeforeSaveTouchesDestination()
    {
        var document = JklParser.ParseDocument(Noncanonical);
        document.Level.Sectors.Reverse();
        var path = Path.Combine(Path.GetTempPath(), "sed-reference-" + Guid.NewGuid().ToString("N") + ".jkl");
        File.WriteAllText(path, "existing output");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Save(document, path));
            Assert.Contains("sector 0", error.Message);
            Assert.Equal("existing output", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AppendingSurfaceThatWouldSplitOriginalSectorRangeRefuses()
    {
        var document = JklParser.ParseDocument(Noncanonical);
        var sector = document.Level.Sectors[1]; // Owns global 0; global 1 belongs to sector 0.
        var added = sector.NewSurface();
        foreach (var vertex in sector.Vertices) added.Corners.Add(new Surface.Corner { Vertex = vertex });
        Assert.Contains("contiguous surface range", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Fact]
    public void SharedImportedVertexSplitRefusesInsteadOfAssigningItsOldIndexArbitrarily()
    {
        var text = Noncanonical.Replace("0: 3\n1: 4\n2: 5", "0: 0\n1: 1\n2: 2")
            .Replace("3,0 4,0 5,0", "0,0 1,0 2,0");
        var document = JklParser.ParseDocument(text);
        var vertex = document.Level.Sectors[0].Vertices.Single(v => v.SourceIndex == 0);
        vertex.Position += new Vec3(0, 0, 2);
        Assert.Contains("World vertex 0 has split", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Fact]
    public void OrphanedSourceSurfaceAllowsUnrelatedHeaderEditButRefusesGeometryRewrite()
    {
        // Remove the second sector using line boundaries; retain its original global surface row.
        int second = Noncanonical.IndexOf("SECTOR 1\n", StringComparison.Ordinal);
        int end = Noncanonical.IndexOf("END\n", second, StringComparison.Ordinal);
        var source = Noncanonical.Remove(second, end - second).Replace("WORLD SECTORS 2", "WORLD SECTORS 1");
        var document = JklParser.ParseDocument(source);
        document.Level.Header.Gravity = 2;
        var output = JklWriter.Build(document);
        Assert.Equal(2, JklParser.Parse(output).Header.Gravity);
        Assert.Equal(2, TableRows(output, "WORLD SURFACES").Count);
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .25f;
        Assert.Contains("unowned", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Fact]
    public void DirectlyMovingAnOriginalSurfaceToAnotherSectorRefuses()
    {
        var document = JklParser.ParseDocument(Noncanonical);
        var a = document.Level.Sectors[0]; var b = document.Level.Sectors[1];
        var surface = a.Surfaces[0];
        a.Surfaces.Remove(surface); b.Surfaces.Add(surface); surface.Sector = b;
        Assert.Contains("changed its sector owner", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Fact]
    public void RemovingAnOriginalSurfaceRefusesEvenWhenOtherNumericIndicesRemainValid()
    {
        var document = JklParser.ParseDocument(Noncanonical);
        document.Level.Sectors[1].Surfaces.Clear();
        Assert.Contains("global surface 0", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClosedNoncanonicalRoomsRetainCogSurfaceReferent(bool appendSector)
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        level.ColorMaps.Add("dflt.cmp");
        level.Sectors.Add(SectorFactory.CreateBox(level, new Vec3(0, 0, 0), 1, "", -1));
        level.Sectors.Add(SectorFactory.CreateBox(level, new Vec3(10, 0, 0), 1, "", -1));
        var cog = new Cog { Name = "original-synthetic.cog" }; cog.Values.Add("0"); level.Cogs.Add(cog);
        // Swap whole contiguous ranges in the serialized SECTORS section only.
        // Physical surface zero stays where it was; sector ownership is now reversed.
        var text = JklWriter.Build(new JklDocument(level))
            .Replace("SURFACES\t0\t6", "SURFACES\tSWAP\t6")
            .Replace("SURFACES\t6\t6", "SURFACES\t0\t6")
            .Replace("SURFACES\tSWAP\t6", "SURFACES\t6\t6");
        // Drop optional sector vertex lists/bounds, which still describe the old
        // owner. Parsing derives exactly the vertices of each referenced room.
        var lines = text.Split('\n').ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].StartsWith("VERTICES\t", StringComparison.Ordinal))
            {
                int count = int.Parse(lines[i].Split('\t')[1]); lines.RemoveRange(i, count + 1);
            }
            else if (lines[i].StartsWith("BOUNDBOX\t", StringComparison.Ordinal)
                || lines[i].StartsWith("CENTER\t", StringComparison.Ordinal)
                || lines[i].StartsWith("RADIUS\t", StringComparison.Ordinal)) lines.RemoveAt(i);
        }
        var document = JklParser.ParseDocument(string.Join('\n', lines));
        Assert.DoesNotContain(ConsistencyChecker.Check(document.Level), i => i.Severity == IssueSeverity.Error);
        var witness = document.Level.Sectors[1].Surfaces[0]; // Global surface zero.
        var position = witness.Corners.Select(c => c.Vertex.Position).ToArray();
        var normal = witness.Normal;
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .375f;
        if (appendSector) document.Level.Sectors.Add(SectorFactory.CreateBox(document.Level, new Vec3(20, 0, 0), 1, "", -1));
        var output = JklWriter.Build(document);
        var reopened = JklParser.ParseDocument(output);

        Assert.DoesNotContain(ConsistencyChecker.Check(reopened.Level), i => i.Severity == IssueSeverity.Error);
        int referenced = int.Parse(Assert.Single(reopened.Level.Cogs[0].Values));
        Assert.Equal(0, referenced);
        // Resolve the numeric witness through the ACTUAL emitted surface row,
        // then resolve its vertex indices through the actual emitted vertex table.
        var row = TableRows(output, "World surfaces")[referenced];
        var vertices = TableRows(output, "World vertices");
        var actual = row.Skip(10).Take(int.Parse(row[9])).Select(token =>
        {
            var vertex = vertices[int.Parse(token.Split(',')[0])];
            return new Vec3(double.Parse(vertex[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(vertex[2], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(vertex[3], System.Globalization.CultureInfo.InvariantCulture));
        }).ToArray();
        Assert.Equal(position, actual);
        Assert.Equal(position, reopened.Level.Sectors[1].Surfaces[0].Corners.Select(c => c.Vertex.Position));
        Assert.Equal(normal, reopened.Level.Sectors[1].Surfaces[0].Normal);
        Assert.Equal(.375f, reopened.Level.Sectors[0].Surfaces[0].ExtraLightIntensity);
        Assert.Equal(output, JklWriter.Build(reopened));
    }

    [Fact]
    public void RetainedWorldVertexSlotsKeepPositiveAndNegativeZeroDistinct()
    {
        var document = JklParser.ParseDocument(Noncanonical.Replace("0: 10 0 0", "0: -0 0 0"));
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .125f;
        var reopened = JklParser.ParseDocument(JklWriter.Build(document));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(reopened.Level.Sectors[1].Vertices.Single(v => v.SourceIndex == 0).Position.X));
        Assert.Equal(0L, BitConverter.DoubleToInt64Bits(reopened.Level.Sectors[0].Vertices.Single(v => v.SourceIndex == 3).Position.X));
    }

    [Theory]
    [InlineData("1", "0")]
    [InlineData("0", "0")]
    [InlineData("0", "3")]
    public void NonsequentialSectorLabelsRemainReadableButCannotRegenerateGeometry(string first, string second)
    {
        var text = Noncanonical.Replace("SECTOR 0\n", "SECTOR FIRST\n")
            .Replace("SECTOR 1\n", $"SECTOR {second}\n").Replace("SECTOR FIRST\n", $"SECTOR {first}\n");
        var document = JklParser.ParseDocument(text);
        Assert.Contains(document.PreservationWarnings, warning => warning.Contains("source label", StringComparison.Ordinal));
        document.Level.Header.Gravity = 2;
        Assert.Contains($"SECTOR {second}\n", JklWriter.Build(document));
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .25f;
        Assert.Contains("source label", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Fact]
    public void SharedVertexCopiesWithOppositeZeroSignsCannotSilentlyChooseOneSign()
    {
        var text = Noncanonical.Replace("0: 10 0 0", "0: 0 0 0").Replace("3,0 4,0 5,0", "0,0 1,0 2,0");
        var document = JklParser.ParseDocument(text);
        var vertex = document.Level.Sectors[0].Vertices.Single(v => v.SourceIndex == 0);
        vertex.Position = new Vec3(BitConverter.Int64BitsToDouble(long.MinValue), vertex.Position.Y, vertex.Position.Z);
        Assert.Contains("World vertex 0 has split", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Theory]
    [InlineData("WORLD ADJOINS 1\n0: 0x3 99 0", "0")]
    [InlineData("WORLD ADJOINS 0", "5")]
    public void UnsupportedImportedAdjoinIsReadableButNotSilentlyDroppedByGeometryRewrite(string adjoins, string index)
    {
        var text = Noncanonical.Replace("WORLD ADJOINS 0", adjoins)
            .Replace("0: -1 0 0 4 3 3 -1", $"0: -1 0 0 4 3 3 {index}");
        var document = JklParser.ParseDocument(text);
        document.Level.Header.Gravity = 2;
        var unchangedGeometry = JklWriter.Build(document);
        Assert.Equal(index, TableRows(unchangedGeometry, "WORLD SURFACES")[0][7]);
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .25f;
        var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document));
        Assert.Contains("adjoin", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectInvalidAdjoinRefusesWithoutMaterialRegistration(bool outsideDocument)
    {
        var document = JklParser.ParseDocument(Noncanonical);
        var surface = document.Level.Sectors[0].Surfaces[0];
        surface.Material = "new-registration-must-not-persist.mat";
        surface.Adjoin = outsideDocument ? SectorFactory.CreateBox(document.Level, new Vec3(30, 0, 0), 1, "", -1).Surfaces[0]
            : document.Level.Sectors[1].Surfaces[0];
        var materials = document.Materials.ToArray();
        Assert.Contains("reciprocal partner", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
        Assert.Equal(materials, document.Materials);
    }

    [Theory]
    [InlineData("0: 10 0 0", "0: 99 0 0")]
    [InlineData("SURFACES 1 1", "SURFACES 0 1")]
    public void MutatedRetainedGeometryCannotReuseCapturedSourceIdentities(string oldLine, string newLine)
    {
        var document = JklParser.ParseDocument(Noncanonical);
        int row = Array.FindIndex(document.SourceLines, line => line.Trim() == oldLine);
        Assert.True(row >= 0);
        document.SourceLines[row] = newLine;
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .375f;
        document.Level.Sectors[0].Surfaces[0].Material = "must-not-register.mat";
        var materials = document.Materials.ToArray();
        var path = Path.Combine(Path.GetTempPath(), "sed-source-identity-" + Guid.NewGuid().ToString("N") + ".jkl");
        File.WriteAllText(path, "existing output");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Save(document, path));
            Assert.Contains("Source geometry text changed", error.Message);
            Assert.Equal("existing output", File.ReadAllText(path));
            Assert.Equal(materials, document.Materials);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("wall.mat")]
    [InlineData("WALL.MAT")]
    public void ExistingMatchingMaterialSlotSurvivesDuplicateCaseInsensitiveNames(string secondName)
    {
        var text = Noncanonical.Replace("SECTION: GEORESOURCE", $"SECTION: MATERIALS\nWORLD MATERIALS 2\n0: wall.mat 1 1\n1: {secondName} 2 2\nEND\nSECTION: GEORESOURCE")
            .Replace("0: -1 0 0 4 3 3 -1", "0: 1 0 0 4 3 3 -1")
            .Replace("1: -1 0 0 4 3 3 -1", "1: 0 0 0 4 3 3 -1");
        var document = JklParser.ParseDocument(text);
        Assert.Equal(1, document.Level.Sectors[1].Surfaces[0].MaterialIndex);
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .375f;
        var output = JklWriter.Build(document);
        Assert.Equal("1", TableRows(output, "World surfaces")[0][1]);
        Assert.Equal("0", TableRows(output, "World surfaces")[1][1]);
        var reopened = JklParser.ParseDocument(output);
        Assert.Equal(1, reopened.Level.Sectors[1].Surfaces[0].MaterialIndex);
        Assert.Equal(secondName, reopened.Materials[1]);
        Assert.Contains($"1: {secondName} 2 2", output);
        Assert.Equal(output, JklWriter.Build(reopened));
    }

    [Fact]
    public void OverlappingRangesAndAnOrphanCannotPassCoverageByMatchingTotalCount()
    {
        var source = Noncanonical.Replace("SURFACES 1 1", "SURFACES 0 1");
        var document = JklParser.ParseDocument(source);
        Assert.Equal(2, document.Level.Sectors.Sum(s => s.Surfaces.Count));
        Assert.Equal(2, TableRows(source, "WORLD SURFACES").Count);
        Assert.Equal(source, JklWriter.Build(document));
        document.Level.Header.Gravity = 2;
        var headerOnly = JklWriter.Build(document);
        Assert.Equal(2f, JklParser.Parse(headerOnly).Header.Gravity);
        Assert.Equal(2, TableRows(headerOnly, "WORLD SURFACES").Count);
        document.Level.Sectors[0].Surfaces[0].ExtraLightIntensity = .25f;
        Assert.Contains("global surface 1", Assert.Throws<InvalidOperationException>(() => JklWriter.Build(document)).Message);
    }

    [Fact]
    public void LateUnmodeledSectionRefusalDoesNotCommitProposedMaterialRegistrations()
    {
        var source = Noncanonical.Replace("SECTOR 0\n", "SECTOR 0\nORIGINAL_SYNTHETIC_EXTENSION 42\n");
        var document = JklParser.ParseDocument(source);
        document.Level.Sectors[0].Ambient = new ColorF(.25f, .25f, .25f);
        document.Level.Sectors[0].Surfaces[0].Material = "must-not-register-after-refusal.mat";
        var materials = document.Materials.ToArray();
        var path = Path.Combine(Path.GetTempPath(), "sed-late-export-refusal-" + Guid.NewGuid().ToString("N") + ".jkl");
        File.WriteAllText(path, "existing output");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => JklWriter.Save(document, path));
            Assert.Contains("Cannot rewrite SECTORS", error.Message);
            Assert.Equal(materials, document.Materials);
            Assert.Equal("existing output", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    private static List<string[]> TableRows(string text, string table)
    {
        var lines = text.Split('\n');
        int start = Array.FindIndex(lines, line => line.TrimStart().StartsWith(table + " ", StringComparison.OrdinalIgnoreCase));
        int count = int.Parse(lines[start].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1]);
        return lines.Skip(start + 1).Take(count)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToList();
    }
}

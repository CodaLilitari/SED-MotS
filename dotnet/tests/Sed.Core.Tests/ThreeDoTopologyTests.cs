using System.Globalization;
using System.Text;
using Sed.Formats.ThreeDo;

namespace Sed.Core.Tests;

public sealed class ThreeDoTopologyTests
{
    [Theory]
    [InlineData(2.1)]
    [InlineData(2.2)]
    public void ReadsEveryDeclaredGeometrySetWithoutLoadingItsMeshes(double version)
    {
        int[][] shapes = { new[] { 3, 4 }, new[] { 2 }, new[] { 1, 2 }, Array.Empty<int>() };
        string source = ArchitectureTopologyFixture.Build(shapes, version);
        ThreeDoTopology result = ThreeDoTopologyReader.Read(source);
        Assert.Equal(version, result.Version);
        Assert.Equal(4, result.Geosets.Count);
        for (int i = 0; i < shapes.Length; i++) Assert.Equal(shapes[i], result.Geosets[i].VertexCounts);
        Assert.Equal(ArchitectureModelFingerprint.Compute(source), result.ArchitectureContentHash);
    }

    [Fact]
    public void TopologyCanBeKnownWhenLegacyFingerprintNormalizationIsUnsupported()
    {
        string source = "# Non-ASCII annotation: \u00e9\n" + ArchitectureTopologyFixture.Build(new[] { new[] { 3 } });
        ThreeDoTopology result = ThreeDoTopologyReader.Read(source);
        Assert.Equal(new[] { 3 }, result.Geosets[0].VertexCounts);
        Assert.Null(result.ArchitectureContentHash);
    }

    [Fact]
    public void AcceptsEditorOptionalMetadataNormalsAndLegacyScalarOmission()
    {
        const string source = """
            SECTION: HEADER
            3DO 2.1
            END
            SECTION: GEOMETRYDEF
            GEOSETS 1
            GEOSET 0
            MESHES 1
            MESH 0
            VERTICES 1
            0: 0 0 0
            TEXTURE VERTICES 0
            FACES 0
            END
            """;
        Assert.Equal(new[] { 1 }, ThreeDoTopologyReader.Read(source).Geosets[0].VertexCounts);
    }

    [Fact]
    public void CountlessNormalHeadersAndCaseWhitespaceVariantsRemainAccepted()
    {
        string source = ArchitectureTopologyFixture.Build(new[] { new[] { 3 } })
            .Replace("VERTEX NORMALS 3", "vertex\t normals")
            .Replace("FACE NORMALS 1", "face   normals")
            .Replace("TEXTURE VERTICES 3", "texture\tvertices\t3")
            .Replace("GEOSET 0", "geoset\t0");
        Assert.Equal(new[] { 3 }, ThreeDoTopologyReader.Read(source).Geosets[0].VertexCounts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DataRowLabelsDoNotReindexEngineArrays(bool repeat)
    {
        string source = ArchitectureTopologyFixture.Build(new[] { new[] { 3, 4 }, new[] { 2 } });
        int label = 30;
        source = System.Text.RegularExpressions.Regex.Replace(source, @"(?m)^\d+:",
            _ => (repeat ? 4 : label--).ToString(CultureInfo.InvariantCulture) + ":");
        ThreeDoTopology result = ThreeDoTopologyReader.Read(source);
        Assert.Equal(new[] { 3, 4 }, result.Geosets[0].VertexCounts);
        Assert.Equal(new[] { 2 }, result.Geosets[1].VertexCounts);
    }

    [Theory]
    [MemberData(nameof(MalformedSources))]
    public void RefusesMalformedAllLodTables(string label, string source)
    {
        var error = Assert.Throws<FormatException>(() => ThreeDoTopologyReader.Read(source));
        Assert.False(string.IsNullOrWhiteSpace(error.Message), label);
    }

    public static IEnumerable<object[]> MalformedSources()
    {
        string source = ArchitectureTopologyFixture.Build(new[] { new[] { 3 } });
        foreach (var (before, after, label) in new[] {
            ("GEOSETS 1", "GEOSETS 0", "empty geometry"),
            ("GEOSETS 1", "GEOSETS 5", "engine supports at most four LODs"),
            ("GEOSETS 1", "GEOSETS -1", "negative LOD count"),
            ("GEOSETS 1", "GEOSETS 2147483647", "huge LOD count"),
            ("GEOSETS 1", "GEOSETS 1\nGEOSETS 1", "duplicate LOD count"),
            ("GEOSET 0", "GEOSET 1", "out-of-order LOD"),
            ("MESHES 1", "MESHES 4097", "huge mesh count"),
            ("MESHES 1", "MESHES 1\nMESHES 1", "duplicate mesh count"),
            ("MESH 0", "MESH 1", "out-of-order mesh"),
            ("VERTICES 3", "VERTICES 1000001", "huge vertex or UV count"),
            ("VERTICES 3\n0:", "VERTICES 3\nVERTICES 3\n0:", "duplicate vertex header"),
            ("1: 1 0 0 0.25", "1: 1 0 0 0.25\n1: 1 0 0 0.25", "extra vertex row beyond declared count"),
            ("0: 0 0 0 0.25", "0: NaN 0 0 0.25", "nonfinite vertex"),
            ("TEXTURE VERTICES 3", "FACES 1", "out-of-order tables"),
            ("VERTEX NORMALS 3", "VERTEX NORMALS 2", "normal count mismatch"),
            ("FACES 1", "FACES 1000001", "huge face count"),
            ("3 0,0 1,1 2,2", "3 0,0 1,1", "truncated face corners"),
            ("3 0,0 1,1 2,2", "3 0,0 1,1 3,2", "missing vertex reference"),
            ("SECTION: HIERARCHYDEF", "GEOSET 0\nMESHES 0\nSECTION: HIERARCHYDEF", "undeclared duplicate LOD"),
            ("SECTION: HIERARCHYDEF", "SECTION: GEOMETRYDEF\nGEOSETS 1\nGEOSET 0\nMESHES 0\nSECTION: HIERARCHYDEF", "duplicate geometry section"),
            ("3DO 2.2", "3DO 2.3", "unsupported architecture format"),
        }) yield return new object[] { label, source.Replace(before, after) };
        foreach (string header in new[] { "VERTICES 3", "TEXTURE VERTICES 3", "VERTEX NORMALS 3", "FACES 1", "FACE NORMALS 1" })
            yield return new object[] { "truncated " + header, source[..(source.IndexOf(header, StringComparison.Ordinal) + header.Length)] };
        string two = ArchitectureTopologyFixture.Build(new[] { new[] { 3 }, new[] { 2 } });
        yield return new object[] { "truncated lower LOD", two[..(two.LastIndexOf("VERTICES 2", StringComparison.Ordinal) + "VERTICES 2".Length)] };
        yield return new object[] { "duplicate lower LOD", two.Replace("GEOSET 1", "GEOSET 0") };
        yield return new object[] { "missing declared lower LOD", source.Replace("GEOSETS 1", "GEOSETS 2") };
        yield return new object[] { "excessively long line", source + "\n#" + new string('x', ThreeDoTopologyReader.MaxLineCharacters) };
    }
}

/// <summary>Original generated format fixtures, never recovered game model text.</summary>
internal static class ArchitectureTopologyFixture
{
    public static string Build(int[][] shapes, double version = 2.2)
    {
        var text = new StringBuilder();
        void W(string line) => text.Append(line).Append('\n');
        W("SECTION: HEADER"); W("3DO " + version.ToString("0.0", CultureInfo.InvariantCulture));
        W("SECTION: MODELRESOURCE"); W("MATERIALS 1"); W("0: synthetic.mat");
        W("SECTION: GEOMETRYDEF"); W("RADIUS 2"); W("INSERT OFFSET 0 0 0");
        W($"GEOSETS {shapes.Length}");
        for (int g = 0; g < shapes.Length; g++)
        {
            W($"GEOSET {g}"); W($"MESHES {shapes[g].Length}");
            for (int m = 0; m < shapes[g].Length; m++)
            {
                int n = shapes[g][m];
                W($"MESH {m}"); W($"NAME Synthetic_{g}_{m}"); W("RADIUS 2");
                W("GEOMETRYMODE 4"); W("LIGHTINGMODE 3"); W("TEXTUREMODE 3");
                W($"VERTICES {n}");
                for (int i = 0; i < n; i++) W($"{i}: {i} 0 0 0.25");
                W($"TEXTURE VERTICES {n}");
                for (int i = 0; i < n; i++) W($"{i}: {i} 0");
                W($"VERTEX NORMALS {n}");
                for (int i = 0; i < n; i++) W($"{i}: 0 0 1");
                W($"FACES {(n >= 3 ? 1 : 0)}");
                if (n >= 3) W("0: 0 0x0 4 3 3 0 3 0,0 1,1 2,2");
                W($"FACE NORMALS {(n >= 3 ? 1 : 0)}");
                if (n >= 3) W("0: 0 0 1");
            }
        }
        W("SECTION: HIERARCHYDEF"); W("HIERARCHY NODES 0"); W("END");
        return text.ToString();
    }
}

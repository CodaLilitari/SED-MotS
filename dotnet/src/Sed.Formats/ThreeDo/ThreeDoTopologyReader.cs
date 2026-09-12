using Sed.Formats.Jkl;

namespace Sed.Formats.ThreeDo;

/// <summary>Vertex-array shape of one geometry set, in source mesh order.</summary>
public sealed record ThreeDoGeosetTopology(IReadOnlyList<int> VertexCounts);

/// <summary>All declared JK/MotS geometry sets; this does not certify the origin or quality of a lighting bake.</summary>
public sealed record ThreeDoTopology(double Version, IReadOnlyList<ThreeDoGeosetTopology> Geosets, string? ArchitectureContentHash);

/// <summary>
/// Bounded geometry-table reader for architectural array coverage. It does not
/// render lower LODs or replace the full model parser. Optional mesh metadata
/// and normals accepted by the editor remain optional, but every declared table
/// must be complete and ordered, including geometry sets beyond geoset zero.
/// Indexed data-row labels are informational; engine arrays use physical row order.
/// </summary>
public static class ThreeDoTopologyReader
{
    public const int MaxGeosets = 4;
    public const int MaxMeshesPerGeoset = 4096;
    public const int MaxAggregateVertices = 1_000_000;
    public const int MaxAggregateUvs = 1_000_000;
    public const int MaxAggregateFaces = 1_000_000;
    public const int MaxSourceCharacters = 128 * 1024 * 1024;
    public const int MaxLineCharacters = 16_384;

    public static ThreeDoTopology Read(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaxSourceCharacters) throw new FormatException("3DO topology source exceeds its character budget.");
        var reader = new Lines(source);
        double version = 0;
        bool headerSeen = false, geometrySeen = false, versionSeen = false;
        IReadOnlyList<ThreeDoGeosetTopology>? geosets = null;
        string section = "";
        while (reader.Peek() is { } line)
        {
            reader.Take();
            var tokens = JklReader.Tokens(line);
            string keyword = tokens[0].ToUpperInvariant();
            if (keyword == "SECTION:")
            {
                if (tokens.Length != 2) throw reader.Error("Invalid 3DO section header.");
                section = tokens[1].ToUpperInvariant();
                if (section == "HEADER")
                {
                    if (headerSeen || geometrySeen) throw reader.Error("Duplicate or out-of-order 3DO header.");
                    headerSeen = true;
                }
                else if (section == "GEOMETRYDEF")
                {
                    if (!versionSeen || geometrySeen) throw reader.Error("Missing header or duplicate 3DO geometry section.");
                    geometrySeen = true;
                    geosets = ReadGeometry(reader);
                    section = "";
                }
                continue;
            }
            if (keyword == "END") { section = ""; continue; }
            if (section == "HEADER" && keyword == "3DO")
            {
                if (versionSeen || tokens.Length != 2) throw reader.Error("Duplicate or invalid 3DO version.");
                version = JklReader.ParseFloat(tokens[1]);
                if (version != 2.1 && version != 2.2) throw reader.Error("Architectural topology supports 3DO 2.1 and 2.2.");
                versionSeen = true;
            }
            else if (keyword is "GEOSETS" or "GEOSET" or "MESHES" or "MESH" or "VERTICES" or "FACES")
                throw reader.Error("3DO topology header appears outside its geometry section.");
        }
        if (!versionSeen || !geometrySeen || geosets is null) throw reader.Error("Missing 3DO header or geometry section.");
        return new ThreeDoTopology(version, geosets, ArchitectureModelFingerprint.Compute(source));
    }

    private static IReadOnlyList<ThreeDoGeosetTopology> ReadGeometry(Lines reader)
    {
        // RADIUS and INSERT OFFSET are not topology, but recognize them only in
        // their legal prefix positions so a misplaced count cannot be skipped.
        if (reader.Has("RADIUS")) ReadNumbers(reader, "RADIUS", 1);
        if (reader.Has("INSERT", "OFFSET")) ReadNumbers(reader, "INSERT OFFSET", 3);
        int count = ReadCount(reader, "GEOSETS", MaxGeosets);
        if (count == 0) throw reader.Error("3DO has no geometry sets.");
        long vertices = 0, uvs = 0, faces = 0;
        var result = new List<ThreeDoGeosetTopology>(count);
        for (int g = 0; g < count; g++)
        {
            ReadIndexHeader(reader, "GEOSET", g);
            int meshCount = ReadCount(reader, "MESHES", MaxMeshesPerGeoset);
            var shape = new List<int>(meshCount);
            for (int m = 0; m < meshCount; m++)
            {
                ReadIndexHeader(reader, "MESH", m);
                if (reader.Has("NAME"))
                {
                    var name = reader.Tokens();
                    if (name.Length != 2) throw reader.Error("Invalid 3DO mesh name header.");
                }
                if (reader.Has("RADIUS")) ReadNumbers(reader, "RADIUS", 1);
                foreach (string mode in new[] { "GEOMETRYMODE", "LIGHTINGMODE", "TEXTUREMODE" })
                    if (reader.Has(mode)) ReadCount(reader, mode, int.MaxValue);
                int nv = ReadCount(reader, "VERTICES", MaxAggregateVertices);
                vertices += nv;
                if (vertices > MaxAggregateVertices) throw reader.Error("3DO exceeds the aggregate vertex budget.");
                ReadNumericRows(reader, nv, 3, 4, "vertex");
                shape.Add(nv);

                int nuv = ReadCount(reader, "TEXTURE VERTICES", MaxAggregateUvs);
                uvs += nuv;
                if (uvs > MaxAggregateUvs) throw reader.Error("3DO exceeds the aggregate UV budget.");
                ReadNumericRows(reader, nuv, 2, 2, "texture vertex");
                if (reader.Has("VERTEX", "NORMALS")) ReadNormals(reader, "VERTEX NORMALS", nv);

                int nf = ReadCount(reader, "FACES", MaxAggregateFaces);
                faces += nf;
                if (faces > MaxAggregateFaces) throw reader.Error("3DO exceeds the aggregate face budget.");
                for (int f = 0; f < nf; f++) ReadFace(reader, nv);
                if (reader.Has("FACE", "NORMALS")) ReadNormals(reader, "FACE NORMALS", nf);
            }
            result.Add(new ThreeDoGeosetTopology(shape.AsReadOnly()));
        }
        if (reader.Peek() is { } next && !IsBoundary(next))
            throw reader.Error("Unexpected data after the declared 3DO geometry sets; duplicate or out-of-order table.");
        return result.AsReadOnly();
    }

    private static bool IsBoundary(string line)
    {
        string first = JklReader.FirstWord(line);
        return first.Equals("SECTION:", StringComparison.OrdinalIgnoreCase) || first.Equals("END", StringComparison.OrdinalIgnoreCase);
    }

    private static void ReadNormals(Lines reader, string header, int expected)
    {
        string[] tokens = reader.Tokens();
        if (tokens.Length != 2 && tokens.Length != 3) throw reader.Error("Invalid 3DO normal table header.");
        if (tokens.Length == 3 && ParseCount(tokens[2], MaxAggregateVertices) != expected)
            throw reader.Error("3DO normal count differs from its geometry table.");
        ReadNumericRows(reader, expected, 3, 3, header);
    }

    private static void ReadNumericRows(Lines reader, int count, int minimum, int maximum, string label)
    {
        for (int i = 0; i < count; i++)
        {
            string[] tokens = IndexedTokens(reader);
            if (tokens.Length < minimum || tokens.Length > maximum) throw reader.Error($"Invalid or truncated 3DO {label} row.");
            foreach (string value in tokens) JklReader.ParseFloat(value);
        }
    }

    private static void ReadFace(Lines reader, int vertices)
    {
        string[] tokens = IndexedTokens(reader);
        if (tokens.Length < 7) throw reader.Error("Truncated 3DO face row.");
        JklReader.ParseInt(tokens[0]); JklReader.ParseHex(tokens[1]);
        JklReader.ParseInt(tokens[2]); JklReader.ParseInt(tokens[3]); JklReader.ParseInt(tokens[4]);
        JklReader.ParseFloat(tokens[5]);
        int corners = ParseCount(tokens[6], 64);
        string[] pairs = JklReader.Tokens(string.Join(' ', tokens.Skip(7)).Replace(',', ' '));
        if (pairs.Length != corners * 2) throw reader.Error("3DO face count differs from its vertex/UV pairs.");
        for (int c = 0; c < corners; c++)
        {
            int vertex = JklReader.ParseInt(pairs[c * 2]);
            if ((uint)vertex >= vertices) throw reader.Error("3DO face references a missing vertex.");
            // UVs are unused by a solid MAT, whose identity is outside this
            // topology reader; retain syntax validation without guessing it.
            JklReader.ParseInt(pairs[c * 2 + 1]);
        }
    }

    private static string[] IndexedTokens(Lines reader)
    {
        string line = reader.Take();
        int colon = line.IndexOf(':');
        if (colon <= 0) throw reader.Error("3DO table row requires a numeric label followed by a colon.");
        // rdModel3 reads vertex/UV/normal labels into an unused temporary and
        // discards face labels. Stock MotS crt6-1.3do repeats normal label 4;
        // labels therefore cannot redefine, duplicate or reorder array slots.
        JklReader.ParseInt(line[..colon].Trim());
        return JklReader.Tokens(line[(colon + 1)..]);
    }

    private static void ReadIndexHeader(Lines reader, string header, int expected)
    {
        int actual = ReadCount(reader, header, int.MaxValue);
        if (actual != expected) throw reader.Error($"3DO {header} indices must be sequential without duplicates.");
    }

    private static int ReadCount(Lines reader, string header, int maximum)
    {
        string[] wanted = header.Split(' '), tokens = reader.Tokens();
        if (tokens.Length != wanted.Length + 1 || !Prefix(tokens, wanted)) throw reader.Error($"Missing or out-of-order 3DO {header} header.");
        return ParseCount(tokens[^1], maximum);
    }

    private static int ParseCount(string text, int maximum)
    {
        int value = JklReader.ParseInt(text);
        if (value < 0 || value > maximum) throw new FormatException($"3DO declared count exceeds 0..{maximum}.");
        return value;
    }

    private static void ReadNumbers(Lines reader, string header, int count)
    {
        string[] wanted = header.Split(' '), tokens = reader.Tokens();
        if (tokens.Length != wanted.Length + count || !Prefix(tokens, wanted)) throw reader.Error($"Invalid 3DO {header} header.");
        foreach (string value in tokens.Skip(wanted.Length)) JklReader.ParseFloat(value);
    }

    private static bool Prefix(string[] tokens, string[] wanted) => tokens.Length >= wanted.Length &&
        wanted.Select((value, index) => tokens[index].Equals(value, StringComparison.OrdinalIgnoreCase)).All(matches => matches);

    private sealed class Lines(string source)
    {
        private readonly StringReader _reader = new(source);
        private string? _pending;
        private int _line;
        public string? Peek()
        {
            while (_pending is null && _reader.ReadLine() is { } line)
            {
                _line++;
                if (line.Length > MaxLineCharacters || _line > 5_100_000) throw Error("3DO exceeds its line budget.");
                int comment = line.IndexOf('#');
                if (comment >= 0) line = line[..comment];
                line = line.Trim();
                if (line.Length > 0 && line[0] != ';') _pending = line;
            }
            return _pending;
        }
        public string Take()
        {
            string result = Peek() ?? throw Error("Truncated 3DO topology table.");
            _pending = null;
            return result;
        }
        public string[] Tokens() => JklReader.Tokens(Take());
        public bool Has(params string[] prefix) => Peek() is { } line && Prefix(JklReader.Tokens(line), prefix);
        public FormatException Error(string message) => new($"{message} (line {_line})");
    }
}

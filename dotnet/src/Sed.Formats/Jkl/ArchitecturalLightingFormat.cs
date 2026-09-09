using System.Globalization;
using Sed.Core.Model;

namespace Sed.Formats.Jkl;

/// <summary>Bounded, atomic reader/writer for the MotS ARCHLIGHTING section body.</summary>
public static class ArchitecturalLightingFormat
{
    public static List<ArchitecturalLightObject> Parse(IEnumerable<string> source)
    {
        var lines = source.Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length != 0).ToArray();
        int cursor = 0;
        string[] Next()
        {
            if (cursor == lines.Length) throw new FormatException("Truncated architectural lighting section.");
            return JklReader.Tokens(lines[cursor++]);
        }
        int Count(string value, int maximum)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n > maximum)
                throw new FormatException("Architectural lighting count exceeds available section data or is invalid.");
            return n;
        }
        int Header(string name, int minimumLines)
        {
            var tokens = Next();
            if (tokens.Length != 2 || !tokens[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Expected architectural lighting {name} header.");
            return Count(tokens[1], (lines.Length - cursor) / minimumLines);
        }
        void Index(string label, int expected)
        {
            var t = Next();
            if (t.Length != 2 || !t[0].Equals(label, StringComparison.OrdinalIgnoreCase) ||
                Count(t[1], int.MaxValue) != expected)
                throw new FormatException($"Expected architectural lighting {label} {expected}.");
        }
        var first = Next();
        if (first.Length != 3 || !first[0].Equals("Num", StringComparison.OrdinalIgnoreCase) ||
            !first[1].Equals("ArchObjects", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Expected Num ArchObjects header.");
        int objects = Count(first[2], (lines.Length - cursor) / 2);
        var result = new List<ArchitecturalLightObject>();
        for (int o = 0; o < objects; o++)
        {
            Index("archobject", o);
            int meshes = Header("nummeshes:", 2);
            var obj = new ArchitecturalLightObject();
            for (int m = 0; m < meshes; m++)
            {
                Index("mesh", m);
                int vertices = Header("numvertices:", 1);
                var mesh = new ArchitecturalLightMesh();
                for (int v = 0; v < vertices; v++)
                {
                    var t = Next();
                    if (t.Length != 5 || Count(t[0].TrimEnd(':'), int.MaxValue) != v)
                        throw new FormatException($"Invalid architectural lighting vertex {v}.");
                    mesh.Vertices.Add(new(JklReader.ParseFloat(t[1]), JklReader.ParseFloat(t[2]),
                        JklReader.ParseFloat(t[3]), JklReader.ParseFloat(t[4])));
                }
                obj.Meshes.Add(mesh);
            }
            result.Add(obj);
        }
        if (cursor != lines.Length) throw new FormatException("Unexpected architectural lighting data after declared objects.");
        return result;
    }

    public static List<string> Build(IReadOnlyList<ArchitecturalLightObject> objects)
    {
        var lines = new List<string> { $"Num ArchObjects {objects.Count}" };
        for (int o = 0; o < objects.Count; o++)
        {
            lines.Add($"archobject {o}");
            lines.Add($"nummeshes: {objects[o].Meshes.Count}");
            for (int m = 0; m < objects[o].Meshes.Count; m++)
            {
                var vertices = objects[o].Meshes[m].Vertices;
                lines.Add($"mesh {m}");
                lines.Add($"numvertices: {vertices.Count}");
                for (int v = 0; v < vertices.Count; v++)
                {
                    var light = vertices[v];
                    lines.Add($"{v}: {Number(light.Mono)} {Number(light.Red)} {Number(light.Green)} {Number(light.Blue)}");
                }
            }
        }
        return lines;
    }

    private static string Number(double value) => double.IsFinite(value)
        ? value.ToString("R", CultureInfo.InvariantCulture)
        : throw new InvalidOperationException("Architectural lighting contains a non-finite value.");
}

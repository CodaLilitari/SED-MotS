using Sed.Core.Math;
using Sed.Formats.Jkl;

namespace Sed.Formats.ThreeDo;

/// <summary>
/// Parses the Sith-engine 3DO model text format (per PJ3DO_IO.INC). Reads the
/// MODELRESOURCE materials, the highest-detail geoset's meshes (vertices,
/// texture vertices, base lighting, vertex normals and face/mesh modes) and the
/// HIERARCHYDEF node transforms, pivots, names, flags and types. Reuses
/// <see cref="JklReader"/> for the shared tokenizer.
/// </summary>
public static class ThreeDoParser
{
    public static ThreeDoModel Parse(string text)
    {
        var model = Parse(new StringReader(text));
        model.ArchitectureContentHash = ArchitectureModelFingerprint.Compute(text);
        return model;
    }

    public static ThreeDoModel Parse(TextReader reader)
    {
        var model = new ThreeDoModel();
        var r = new JklReader(reader);

        while (true)
        {
            r.SeekSection();
            if (r.Eof) break;
            var section = r.Section;
            r.EnterSection();
            switch (section)
            {
                case "HEADER": LoadHeader(r, model); break;
                case "MODELRESOURCE": LoadMaterials(r, model); break;
                case "GEOMETRYDEF": LoadGeometry(r, model); break;
                case "HIERARCHYDEF": LoadHierarchy(r, model); break;
                default: r.SkipToNextSection(); break;
            }
        }
        return model;
    }

    private static void LoadHeader(JklReader r, ThreeDoModel model)
    {
        while (r.Next() && !r.EndOfSection)
        {
            var t = JklReader.Tokens(r.Current);
            if (t.Length >= 2 && t[0] == "3DO")
                model.Version = JklReader.ParseFloat(t[1]);
        }
    }

    private static void LoadMaterials(JklReader r, ThreeDoModel model)
    {
        if (!r.Next()) return;
        int n = Count(r.Current, 65536);
        for (int i = 0; i < n; i++)
        {
            if (!r.Next(upper: false) || r.EndOfSection) throw new FormatException("Truncated 3DO material table.");
            // "i: name.mat"
            var name = JklReader.WordAt(JklReader.StripIndex(r.Current), 0);
            if (name.Length > 0) model.Materials.Add(name);
        }
    }

    private static void LoadGeometry(JklReader r, ThreeDoModel model)
    {
        // RADIUS (skip), INSERT OFFSET x y z, GEOSETS n
        r.Next(); // radius
        r.Next();
        var ins = JklReader.Tokens(r.Current);
        if (ins.Length >= 5 && ins[0] == "INSERT")
            model.InsertOffset = new Vec3(JklReader.ParseFloat(ins[2]), JklReader.ParseFloat(ins[3]), JklReader.ParseFloat(ins[4]));

        r.Next(); // GEOSETS n  (we use geoset 0)
        // advance to "GEOSET 0"
        while (r.Next() && !r.EndOfSection)
            if (JklReader.FirstWord(r.Current) == "GEOSET") break;
        if (r.EndOfSection) return;

        if (!r.Next()) return; // MESHES n
        int nmeshes = Count(r.Current, 4096);

        for (int i = 0; i < nmeshes; i++)
        {
            // advance to "MESH"
            while (r.Next() && !r.EndOfSection)
                if (JklReader.FirstWord(r.Current) == "MESH") break;
            if (r.Eof || r.EndOfSection) throw new FormatException("Truncated 3DO mesh table.");

            var mesh = new Mesh3do();
            model.Meshes.Add(mesh);

            if (r.Next()) mesh.Name = JklReader.WordAt(r.Current, 1); // NAME x

            // Preserve mesh modes before VERTICES; rendering combines mesh and face modes.
            int nverts = 0;
            while (r.Next() && !r.EndOfSection)
            {
                var t = JklReader.Tokens(r.Current);
                if (t[0] == "VERTICES") { nverts = Count(r.Current); break; }
                if (t.Length >= 2)
                {
                    if (t[0] == "GEOMETRYMODE") mesh.GeometryMode = JklReader.ParseInt(t[1]);
                    if (t[0] == "LIGHTINGMODE") mesh.LightingMode = JklReader.ParseInt(t[1]);
                    if (t[0] == "TEXTUREMODE") mesh.TextureMode = JklReader.ParseInt(t[1]);
                }
            }
            if (model.Meshes.Sum(m => (long)m.Vertices.Count) + nverts > 1_000_000)
                throw new FormatException("3DO exceeds the one-million decoded vertex budget.");

            for (int j = 0; j < nverts; j++)
            {
                RequireRow(r, "vertices");
                var t = JklReader.Tokens(JklReader.StripIndex(r.Current));
                if (t.Length < 3) throw new FormatException("Truncated 3DO vertex.");
                mesh.Vertices.Add(new Vec3(
                    JklReader.ParseFloat(t.ElementAtOrDefault(0) ?? "0"),
                    JklReader.ParseFloat(t.ElementAtOrDefault(1) ?? "0"),
                    JklReader.ParseFloat(t.ElementAtOrDefault(2) ?? "0")));
                // Legacy editor fixtures sometimes omit this component; zero is
                // an unlit base contribution, not an invented full-bright value.
                mesh.VertexIntensities.Add(ReadLight(t.ElementAtOrDefault(3) ?? "0"));
            }

            RequireRow(r, "texture vertex header");
            var textureHeader = JklReader.Tokens(r.Current);
            if (textureHeader.Length != 3 || textureHeader[0] != "TEXTURE" || textureHeader[1] != "VERTICES")
                throw new FormatException("Missing 3DO texture vertex header.");
            int ntex = Count(r.Current);
            if (model.Meshes.Sum(m => (long)m.Uvs.Count) + ntex > 1_000_000)
                throw new FormatException("3DO exceeds the one-million decoded UV budget.");
            for (int j = 0; j < ntex; j++)
            {
                RequireRow(r, "texture vertices");
                var t = JklReader.Tokens(JklReader.StripIndex(r.Current));
                if (t.Length < 2) throw new FormatException("Truncated 3DO texture vertex.");
                mesh.Uvs.Add(new Vec2(
                    JklReader.ParseFloat(t.ElementAtOrDefault(0) ?? "0"),
                    JklReader.ParseFloat(t.ElementAtOrDefault(1) ?? "0")));
            }
            // advance to FACES n
            int nfaces = 0;
            bool foundFaces = false;
            while (r.Next() && !r.EndOfSection)
            {
                var t = JklReader.Tokens(r.Current);
                if (t[0] == "FACES") { nfaces = Count(r.Current); foundFaces = true; break; }
                if (t.Length >= 2 && t[0] == "VERTEX" && t[1] == "NORMALS")
                {
                    int count = t.Length >= 3 ? Count(r.Current) : mesh.Vertices.Count;
                    if (count != mesh.Vertices.Count) throw new FormatException("3DO vertex normal count differs from vertex count.");
                    for (int normal = 0; normal < count; normal++)
                    {
                        if (!r.Next() || r.EndOfSection) throw new FormatException("Truncated 3DO vertex normals.");
                        var components = JklReader.Tokens(JklReader.StripIndex(r.Current));
                        if (components.Length < 3) throw new FormatException("Truncated 3DO vertex normal.");
                        mesh.VertexNormals.Add(new Vec3(JklReader.ParseFloat(components[0]), JklReader.ParseFloat(components[1]), JklReader.ParseFloat(components[2])));
                    }
                }
            }
            if (!foundFaces) throw new FormatException("Missing 3DO face table header.");
            if (model.Meshes.Sum(m => (long)m.Faces.Count) + nfaces > 1_000_000)
                throw new FormatException("3DO exceeds the one-million decoded face budget.");
            for (int j = 0; j < nfaces; j++)
            {
                RequireRow(r, "faces");
                mesh.Faces.Add(ParseFace(r.Current));
            }
            // Face normals that follow are skipped by the next mesh's MESH scan.
        }
    }

    private static Face3do ParseFace(string line)
    {
        // num: material type geo light tex extralight nverts  v,uv v,uv ...
        var t = JklReader.Tokens(line);
        var face = new Face3do();
        if (t.Length < 8) throw new FormatException("Truncated 3DO face.");
        face.Material = JklReader.ParseInt(t[1]);
        face.FaceFlags = JklReader.ParseHex(t[2]);
        face.Geo = JklReader.ParseInt(t[3]);
        face.Light = JklReader.ParseInt(t[4]);
        face.Tex = JklReader.ParseInt(t[5]);
        face.ExtraLight = ReadLight(t[6]);
        int nverts = JklReader.ParseInt(t[7]);
        if (nverts < 0 || nverts > 64) throw new FormatException("3DO face corner count must be from 0 through 64.");

        // Pairs start at token 8; rejoin and split commas to tolerate "v,uv" or "v, uv".
        var rest = string.Join(' ', t.Skip(8)).Replace(',', ' ');
        var pair = JklReader.Tokens(rest);
        if (pair.Length != nverts * 2) throw new FormatException("3DO face corner count differs from its vertex/UV pairs.");
        for (int k = 0; k < nverts && k * 2 + 1 < pair.Length; k++)
        {
            face.VertexIndices.Add(JklReader.ParseInt(pair[k * 2]));
            face.UvIndices.Add(JklReader.ParseInt(pair[k * 2 + 1]));
        }
        return face;
    }

    private static void LoadHierarchy(JklReader r, ThreeDoModel model)
    {
        if (!r.Next()) return;
        int n = Count(r.Current, 65536);
        for (int i = 0; i < n; i++)
        {
            // Unlike keywords, node names are authored data. Do not uppercase them.
            if (!r.Next(upper: false) || r.EndOfSection) throw new FormatException("Truncated 3DO hierarchy.");
            var t = JklReader.Tokens(JklReader.StripIndex(r.Current));
            // JK/MotS rdModel3 requires all 18 conversions including the row index:
            // flags type mesh parent child sibling numChildren x y z pitch yaw roll pivotx pivoty pivotz name.
            if (t.Length < 17) throw new FormatException("Truncated 3DO hierarchy node.");
            model.Nodes.Add(new HierarchyNode
            {
                Flags = ReadNodeBits(t[0]),
                Type = ReadNodeBits(t[1]),
                Name = t[16],
                Mesh = JklReader.ParseInt(t[2]),
                Parent = JklReader.ParseInt(t[3]),
                Offset = new Vec3(JklReader.ParseFloat(t[7]), JklReader.ParseFloat(t[8]), JklReader.ParseFloat(t[9])),
                Pitch = JklReader.ParseFloat(t[10]),
                Yaw = JklReader.ParseFloat(t[11]),
                Roll = JklReader.ParseFloat(t[12]),
                Pivot = new Vec3(JklReader.ParseFloat(t[13]), JklReader.ParseFloat(t[14]), JklReader.ParseFloat(t[15])),
            });
        }
    }

    private static uint ReadNodeBits(string text)
    {
        long bits = JklReader.ParseHex(text);
        if (bits < 0 || bits > uint.MaxValue) throw new FormatException("3DO hierarchy flags and type must fit 32 bits.");
        return (uint)bits;
    }

    private static int LastInt(string line)
    {
        var t = JklReader.Tokens(line);
        return t.Length == 0 ? 0 : JklReader.ParseInt(t[^1]);
    }

    private static int Count(string line, int maximum = 1_000_000)
    {
        int count = LastInt(line);
        if (count < 0 || count > maximum) throw new FormatException($"3DO declared count exceeds the 0..{maximum} bound.");
        return count;
    }

    private static void RequireRow(JklReader reader, string section)
    {
        if (!reader.Next() || reader.EndOfSection) throw new FormatException($"Truncated 3DO {section}.");
    }

    private static float ReadLight(string text)
    {
        double value = JklReader.ParseFloat(text);
        if (!double.IsFinite(value) || System.Math.Abs(value) > float.MaxValue)
            throw new FormatException("3DO lighting must be a finite representable value.");
        return (float)value;
    }
}

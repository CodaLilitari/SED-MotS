using System.Globalization;
using Sed.Core.Math;

namespace Sed.Formats.ThreeDo;

/// <summary>
/// Serializes a <see cref="ThreeDoModel"/> to the Sith-engine 3DO text format,
/// faithful to the original's <c>T3DO.SaveToFile</c> (3DO_IO.INC): the HEADER /
/// MODELRESOURCE / GEOMETRYDEF / HIERARCHYDEF layout, per-mesh RADIUS from the
/// bounding sphere, sorted texture-vertex deduplication, averaged vertex
/// normals, and the face line layout (<c>i: imat flags geo light tex intensity nverts v,uv …</c>).
/// Round-trips through <see cref="ThreeDoParser"/>.
/// </summary>
public static class ThreeDoWriter
{
    /// <summary>Builds the 3DO text for <paramref name="model"/> (default version 2.1).</summary>
    public static string Build(ThreeDoModel model, double version = 2.1)
    {
        var insertOffset = model.InsertOffset;
        if (!double.IsFinite(insertOffset.X) || !double.IsFinite(insertOffset.Y) || !double.IsFinite(insertOffset.Z))
            throw new FormatException("3DO insertion offset must contain finite coordinates.");

        var sb = new System.Text.StringBuilder();
        void W(string s) => sb.AppendLine(s);
        void WF(string fmt, params object[] args) => W(string.Format(CultureInfo.InvariantCulture, fmt, args));

        var ijim = Math.Abs(version - 2.3) < 1e-9;

        W($"# MODEL {model.Name} created by SED");
        W("");
        W("SECTION: HEADER");
        W("");
        WF("3DO {0:0.0}", version);
        W("");
        W("SECTION: MODELRESOURCE");
        W("");
        WF("MATERIALS {0}", model.Materials.Count);
        for (int i = 0; i < model.Materials.Count; i++)
            WF("{0}: {1}", i, model.Materials[i]);

        W("");
        W("SECTION: GEOMETRYDEF");
        W("");
        WF("RADIUS {0:0.000000}", FindRadius(model));
        W("");
        WF("INSERT OFFSET {0:G17} {1:G17} {2:G17}", insertOffset.X, insertOffset.Y, insertOffset.Z);
        W("");
        W("GEOSETS 1");
        W("");
        W("GEOSET 0");
        W("");
        WF("MESHES {0}", model.Meshes.Count);

        foreach (var mesh in model.Meshes)
        {
            W("");
            WF("MESH {0}", model.Meshes.IndexOf(mesh));
            W($"NAME {mesh.Name}");
            WF("RADIUS {0:0.000000}", FindRadius(mesh));
            WF("GEOMETRYMODE\t{0}", mesh.GeometryMode);
            WF("LIGHTINGMODE\t{0}", mesh.LightingMode);
            WF("TEXTUREMODE\t{0}", mesh.TextureMode);
            W("");

            WF("VERTICES {0}", mesh.Vertices.Count);
            W("");
            for (int j = 0; j < mesh.Vertices.Count; j++)
            {
                var v = mesh.Vertices[j];
                if (ijim)
                    WF("{0}: {1:0.000000} {2:0.000000} {3:0.000000} 0.000000 0.000000 0.000000 1.000000", j, v.X, v.Y, v.Z);
                else
                    WF("{0}: {1:0.000000} {2:0.000000} {3:0.000000} {4:G9}", j, v.X, v.Y, v.Z,
                        j < mesh.VertexIntensities.Count ? mesh.VertexIntensities[j] : 0f);
            }
            W("");

            // Texture-vertex dedup (AddTXVX): faces reference indices into this
            // list, in first-use order.
            var uvList = new List<Vec2>();
            var uvMap = new Dictionary<(double U, double V), int>();
            foreach (var face in mesh.Faces)
            {
                foreach (int uvIndex in face.UvIndices)
                {
                    if ((uint)uvIndex >= (uint)mesh.Uvs.Count) continue;
                    var uv = mesh.Uvs[uvIndex];
                    if (!uvMap.ContainsKey((uv.X, uv.Y)))
                    {
                        uvMap[(uv.X, uv.Y)] = uvList.Count;
                        uvList.Add(uv);
                    }
                }
            }
            var uvRemap = new int[mesh.Uvs.Count];
            for (int j = 0; j < mesh.Uvs.Count; j++)
                uvMap.TryGetValue((mesh.Uvs[j].X, mesh.Uvs[j].Y), out uvRemap[j]);

            WF("TEXTURE VERTICES {0}", uvList.Count);
            W("");
            for (int j = 0; j < uvList.Count; j++)
                WF("{0}: {1:0.00} {2:0.00}", j, uvList[j].X, uvList[j].Y);
            W("");

            // Vertex normals: averaged face normals (recomputed here).
            var vxNormals = new Vec3[mesh.Vertices.Count];
            var vxMarks = new int[mesh.Vertices.Count];
            foreach (var face in mesh.Faces)
            {
                var n = FaceNormal(mesh, face);
                for (int k = 0; k < face.VertexIndices.Count; k++)
                {
                    int vi = face.VertexIndices[k];
                    if ((uint)vi >= (uint)mesh.Vertices.Count) continue;
                    vxNormals[vi] += n;
                    vxMarks[vi]++;
                }
            }
            W("VERTEX NORMALS");
            W("");
            for (int j = 0; j < mesh.Vertices.Count; j++)
            {
                var n = vxMarks[j] != 0 ? vxNormals[j] / vxMarks[j] : Vec3.Zero;
                WF("{0}: {1:0.000000} {2:0.000000} {3:0.000000}", j, n.X, n.Y, n.Z);
            }
            W("");

            WF("FACES {0}", mesh.Faces.Count);
            W("");
            for (int j = 0; j < mesh.Faces.Count; j++)
            {
                var face = mesh.Faces[j];
                string s;
                if (ijim)
                {
                    s = string.Format(CultureInfo.InvariantCulture,
                        "{0}: {1} 0x{2:x} {3} {4} {5} ({6:0.000000}/{7:0.000000}/{8:0.000000}/{9:0.000000}) {10}",
                        j, face.Material, face.FaceFlags, face.Geo, face.Light, face.Tex,
                        face.ExtraLight, face.ExtraLight, face.ExtraLight, 0f, face.VertexIndices.Count);
                }
                else
                {
                    s = string.Format(CultureInfo.InvariantCulture,
                        "{0}: {1} 0x{2:x} {3} {4} {5} {6:0.0000} {7}",
                        j, face.Material, face.FaceFlags, face.Geo, face.Light, face.Tex,
                        face.ExtraLight, face.VertexIndices.Count);
                }
                for (int v = 0; v < face.VertexIndices.Count; v++)
                {
                    int uv = (uint)v < (uint)face.UvIndices.Count ? face.UvIndices[v] : -1;
                    int remapped = (uint)uv < (uint)uvRemap.Length ? uvRemap[uv] : -1;
                    s += $" {face.VertexIndices[v]}, {remapped}";
                }
                W(s);
            }

            W("");
            W("FACE NORMALS");
            W("");
            for (int j = 0; j < mesh.Faces.Count; j++)
            {
                var n = FaceNormal(mesh, mesh.Faces[j]);
                WF("{0}: {1:0.000000} {2:0.000000} {3:0.000000}", j, n.X, n.Y, n.Z);
            }
            W("");
        }

        W("SECTION: HIERARCHYDEF");
        W("");
        WF("HIERARCHY NODES {0}", model.Nodes.Count);
        W("# num: flags: type: mesh: parent: child:  sibling:  numChildren: x:   y:  z:  pitch: yaw: roll: pivotx: pivoty: pivotz: hnodename:");
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            int nc = 0, k = -1, v = -1;
            for (int j = 0; j < model.Nodes.Count; j++)
            {
                if (model.Nodes[j].Parent != i) continue;
                nc++;
                if (k == -1) k = j;
            }
            for (int j = i + 1; j < model.Nodes.Count; j++)
            {
                if (model.Nodes[j].Parent == model.Nodes[i].Parent) { v = j; break; }
            }

            var hn = model.Nodes[i];
            W(string.Format(CultureInfo.InvariantCulture,
                "{0,2}:     0x{16:x}    0x{17:x}    {1,2}     {2,2}      {3,2}     {4,2}       {5,2}     {6:G17} {7:G17} {8:G17}  {9:G17} {10:G17} {11:G17} {12:G17} {13:G17} {14:G17} {15}",
                i, hn.Mesh, hn.Parent, k, v, nc,
                hn.Offset.X, hn.Offset.Y, hn.Offset.Z, hn.Pitch, hn.Yaw, hn.Roll,
                hn.Pivot.X, hn.Pivot.Y, hn.Pivot.Z, NodeName(model, hn), hn.Flags, hn.Type));
        }

        return sb.ToString();
    }

    private static string NodeName(ThreeDoModel model, HierarchyNode hn)
    {
        string name;
        if (hn.Name.Length > 0)
            name = hn.Name;
        else if ((uint)hn.Mesh < (uint)model.Meshes.Count && model.Meshes[hn.Mesh].Name.Length > 0)
            name = model.Meshes[hn.Mesh].Name;
        else
            name = "$$DUMMY";
        if (name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '#'))
            throw new FormatException("3DO hierarchy names must be single tokens without comments or control characters.");
        return name;
    }

    /// <summary>Writes the model to <paramref name="path"/>.</summary>
    public static void Save(ThreeDoModel model, string path, double version = 2.1) =>
        File.WriteAllText(path, Build(model, version));

    private static double FindRadius(ThreeDoModel model) =>
        model.Meshes.Count == 0 ? 0 : model.Meshes.Max(FindRadius);

    private static double FindRadius(Mesh3do mesh)
    {
        double result = 0;
        foreach (var v in mesh.Vertices)
            result = Math.Max(result, v.Length);
        return result;
    }

    /// <summary>Newell-style face normal from the mesh's vertex positions.</summary>
    private static Vec3 FaceNormal(Mesh3do mesh, Face3do face)
    {
        double nx = 0, ny = 0, nz = 0;
        int n = face.VertexIndices.Count;
        for (int i = 0; i < n; i++)
        {
            int a = face.VertexIndices[i];
            int b = face.VertexIndices[(i + 1) % n];
            if ((uint)a >= (uint)mesh.Vertices.Count || (uint)b >= (uint)mesh.Vertices.Count)
                return Vec3.Zero;
            var cur = mesh.Vertices[a];
            var nxt = mesh.Vertices[b];
            nx += (cur.Y - nxt.Y) * (cur.Z + nxt.Z);
            ny += (cur.Z - nxt.Z) * (cur.X + nxt.X);
            nz += (cur.X - nxt.X) * (cur.Y + nxt.Y);
        }
        return new Vec3(nx, ny, nz).Normalized();
    }
}

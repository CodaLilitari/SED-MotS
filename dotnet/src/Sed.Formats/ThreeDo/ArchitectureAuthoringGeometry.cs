using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Formats.ThreeDo;

/// <summary>Bounded static model transforms for the explicitly unoccluded source-points profile.</summary>
internal static class ArchitectureAuthoringGeometry
{
    internal static IReadOnlyList<IReadOnlyList<Vec3>> Prepare(string source, ThreeDoTopology topology, Thing thing)
    {
        if (topology.Geosets.Count != 1)
            throw Unsupported("reset/bake supports a single geometry set; all-LOD compatibility alone does not establish vertex correspondence");
        var model = ThreeDoParser.Parse(source);
        if (model.Meshes.Count == 0 || model.Meshes.Count != topology.Geosets[0].VertexCounts.Count ||
            model.Meshes.Where((mesh, index) => mesh.Vertices.Count != topology.Geosets[0].VertexCounts[index]).Any())
            throw Unsupported("decoded geoset zero differs from the captured topology");
        if (model.InsertOffset != Vec3.Zero || model.Nodes.Any(node => node.Pivot != Vec3.Zero))
            throw Unsupported("nonzero insert offsets or hierarchy pivots are not supported");
        var parents = ValidateHierarchy(source, model);
        Finite(thing.Position); Finite(new(thing.Pitch, thing.Yaw, thing.Roll));
        var byMesh = new int[model.Meshes.Count]; Array.Fill(byMesh, -1);
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            var node = model.Nodes[i];
            if (node.Flags != 0) throw Unsupported("special hierarchy flags are not supported");
            // Type identifies authored joints (ordinary assets use many values).
            // This profile samples only the stored rest pose, never animation.
            Finite(node.Offset); Finite(new(node.Pitch, node.Yaw, node.Roll));
            if (node.Mesh < -1 || node.Mesh >= model.Meshes.Count) throw Unsupported("hierarchy references a missing mesh");
            if (node.Mesh >= 0)
            {
                if (byMesh[node.Mesh] != -1) throw Unsupported("a mesh has more than one hierarchy instance");
                byMesh[node.Mesh] = i;
            }
        }
        if (model.Nodes.Count > 0 && byMesh.Any(index => index < 0))
            throw Unsupported("every mesh requires exactly one hierarchy instance");
        var result = new List<IReadOnlyList<Vec3>>();
        long transformSteps = 0;
        for (int mesh = 0; mesh < model.Meshes.Count; mesh++)
        {
            var points = new Vec3[model.Meshes[mesh].Vertices.Count];
            var chain = new List<int>();
            for (int node = byMesh[mesh]; node >= 0; node = parents[node]) chain.Add(node);
            transformSteps += (long)points.Length * (chain.Count + 1);
            if (transformSteps > 32_000_000) throw Unsupported("rest-pose work exceeds 32000000 vertex transforms");
            for (int vertex = 0; vertex < points.Length; vertex++)
            {
                var point = model.Meshes[mesh].Vertices[vertex]; Finite(point);
                foreach (int node in chain)
                {
                    var transform = model.Nodes[node];
                    point = Rotate(point, transform.Pitch, transform.Yaw, transform.Roll) + transform.Offset;
                }
                point = Rotate(point, thing.Pitch, thing.Yaw, thing.Roll) + thing.Position;
                Finite(point); points[vertex] = point;
            }
            result.Add(Array.AsReadOnly(points));
        }
        return result.AsReadOnly();
    }

    // Same yaw(Z) * pitch(X) * roll(Y) convention as Mat4.FromPyr, kept
    // double-precision throughout instead of going through render matrices.
    private static Vec3 Rotate(Vec3 point, double pitch, double yaw, double roll)
    {
        const double radians = System.Math.PI / 180;
        double s = System.Math.Sin(roll * radians), c = System.Math.Cos(roll * radians);
        point = new(c * point.X + s * point.Z, point.Y, -s * point.X + c * point.Z);
        s = System.Math.Sin(pitch * radians); c = System.Math.Cos(pitch * radians);
        point = new(point.X, c * point.Y - s * point.Z, s * point.Y + c * point.Z);
        s = System.Math.Sin(yaw * radians); c = System.Math.Cos(yaw * radians);
        return new(c * point.X - s * point.Y, s * point.X + c * point.Y, point.Z);
    }

    private static int[] ValidateHierarchy(string source, ThreeDoModel model)
    {
        var lines = JklParser.SplitLines(source).Select(line => line.Split('#')[0].Trim())
            .Where(line => line.Length > 0 && line[0] != ';').ToArray();
        var headers = lines.Select((text, index) => (text, index))
            .Where(row => JklReader.Tokens(row.text) is var fields && fields.Length >= 2 &&
                fields[0].Equals("SECTION:", StringComparison.OrdinalIgnoreCase) && fields[1].Equals("HIERARCHYDEF", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (headers.Length > 1) throw Unsupported("duplicate hierarchy sections");
        if (headers.Length == 0)
        {
            if (model.Nodes.Count != 0) throw Unsupported("hierarchy source and decoded nodes disagree");
            return [];
        }
        int start = headers[0].index;
        if (JklReader.Tokens(lines[start]).Length != 2 || start + 1 >= lines.Length)
            throw Unsupported("malformed hierarchy header");
        var header = JklReader.Tokens(lines[start + 1]);
        if (header.Length != 3 || !header[0].Equals("HIERARCHY", StringComparison.OrdinalIgnoreCase) ||
            !header[1].Equals("NODES", StringComparison.OrdinalIgnoreCase) || JklReader.ParseInt(header[2]) != model.Nodes.Count)
            throw Unsupported("hierarchy count does not match decoded nodes");
        int count = model.Nodes.Count;
        if (count > 4096) throw Unsupported("rest-pose authoring supports at most 4096 hierarchy nodes");
        var parents = new int[count]; var children = new int[count]; var siblings = new int[count]; var childCounts = new int[count];
        for (int i = 0; i < count; i++)
        {
            if (start + 2 + i >= lines.Length) throw Unsupported("truncated hierarchy");
            string row = lines[start + 2 + i];
            var fields = JklReader.Tokens(JklReader.StripIndex(row));
            if (fields.Length != 17 || row.IndexOf(':') < 1) throw Unsupported("malformed hierarchy row");
            _ = JklReader.ParseInt(row[..row.IndexOf(':')].Trim()); // Physical order, not the numeric label, defines the node slot.
            parents[i] = JklReader.ParseInt(fields[3]); children[i] = JklReader.ParseInt(fields[4]);
            siblings[i] = JklReader.ParseInt(fields[5]); childCounts[i] = JklReader.ParseInt(fields[6]);
            if (parents[i] < -1 || parents[i] >= count || parents[i] == i || children[i] < -1 || children[i] >= count ||
                siblings[i] < -1 || siblings[i] >= count || childCounts[i] < 0 || childCounts[i] > count)
                throw Unsupported("hierarchy references or child counts are invalid");
        }
        int end = start + 2 + count;
        if (end < lines.Length && !lines[end].Equals("END", StringComparison.OrdinalIgnoreCase) &&
            !JklReader.FirstWord(lines[end]).Equals("SECTION:", StringComparison.OrdinalIgnoreCase))
            throw Unsupported("unexpected rows after declared hierarchy");
        if (end < lines.Length && lines[end].Equals("END", StringComparison.OrdinalIgnoreCase) && end + 1 < lines.Length &&
            !JklReader.FirstWord(lines[end + 1]).Equals("SECTION:", StringComparison.OrdinalIgnoreCase))
            throw Unsupported("unexpected rows after hierarchy END");
        if (count == 0) return parents; // Explicit no-hierarchy model: one implicit instance per mesh.
        int[] roots = Enumerable.Range(0, count).Where(i => parents[i] == -1).ToArray();
        if (roots.Length != 1 || siblings[roots[0]] != -1) throw Unsupported("hierarchy must have one root");
        var childrenByParent = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        for (int child = 0; child < count; child++)
            if (parents[child] >= 0) childrenByParent[parents[child]].Add(child);
        for (int parent = 0; parent < count; parent++)
        {
            var expected = childrenByParent[parent];
            var seen = new HashSet<int>();
            for (int child = children[parent]; child != -1; child = siblings[child])
                if (!seen.Add(child) || !expected.Contains(child)) throw Unsupported("hierarchy child/sibling links are cyclic or disagree with parents");
            if (seen.Count != childCounts[parent] || !seen.SetEquals(expected))
                throw Unsupported("hierarchy child/sibling links are incomplete");
        }
        for (int i = 0; i < count; i++)
        {
            var seen = new HashSet<int>();
            for (int parent = i; parent != -1; parent = parents[parent])
            {
                if (!seen.Add(parent)) throw Unsupported("hierarchy parent cycle");
                if (seen.Count > 128) throw Unsupported("rest-pose hierarchy depth exceeds 128 nodes");
            }
        }
        return parents;
    }

    private static void Finite(Vec3 point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
            throw Unsupported("model transforms and positions must remain finite");
    }
    private static InvalidOperationException Unsupported(string detail) => new("Architectural authoring refuses this model: " + detail + ".");
}

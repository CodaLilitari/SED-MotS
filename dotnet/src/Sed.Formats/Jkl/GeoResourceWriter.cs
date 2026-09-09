using System.Globalization;
using System.Text;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Formats.Jkl;

/// <summary>
/// Regenerates the GEORESOURCE and SECTORS sections from the model, faithfully
/// reproducing the original SaveToJKL output (JK/v1): pooled+deduped world
/// vertices and texture vertices, adjoin mirror-pairs, and full surface/sector
/// records. This lets vertex/sector topology edits round-trip to a game-loadable
/// file while every unmodeled section is kept verbatim by <see cref="JklWriter"/>.
/// </summary>
internal static class GeoResourceWriter
{
    public static (List<string> geo, List<string> sectors) Build(Level level, IReadOnlyList<string>? materials = null)
    {
        // 1) Reuse a shared imported vertex only when its source identity and
        // current position both match. Equal coordinates can be distinct topology.
        var vxList = new List<Vec3>();
        var vxKey = new Dictionary<(int, Vec3), int>();
        var vertexGlobal = new Dictionary<Vertex, int>();
        foreach (var sector in level.Sectors)
            foreach (var v in sector.Vertices)
                vertexGlobal[v] = Pool(vxList, vxKey, v);

        // 2) Pool texture vertices (dedup by UV).
        var txList = new List<Vec2>();
        var txKey = new Dictionary<(double, double), int>();
        var cornerTx = new Dictionary<Surface.Corner, int>();
        foreach (var sector in level.Sectors)
            foreach (var surf in sector.Surfaces)
                foreach (var c in surf.Corners)
                    cornerTx[c] = PoolUv(txList, txKey, c.Uv);

        // 3) Global surface numbering + adjoin records (mirror-pairs).
        var surfNum = new Dictionary<Surface, int>();
        int gs = 0;
        foreach (var sector in level.Sectors)
            foreach (var surf in sector.Surfaces)
                surfNum[surf] = gs++;

        var adjOf = new Dictionary<Surface, int>();
        var adjFlags = new List<long>();
        var adjOwner = new List<int>();
        foreach (var sector in level.Sectors)
            foreach (var surf in sector.Surfaces)
            {
                if (surf.Adjoin is not null && surfNum.ContainsKey(surf.Adjoin))
                {
                    adjOf[surf] = adjFlags.Count;
                    adjFlags.Add(surf.AdjoinFlags);
                    adjOwner.Add(surfNum[surf]);
                }
                else adjOf[surf] = -1;
            }
        var mirror = new int[adjFlags.Count];
        foreach (var sector in level.Sectors)
            foreach (var surf in sector.Surfaces)
                if (surf.Adjoin is not null && adjOf.TryGetValue(surf.Adjoin, out int m) && adjOf[surf] >= 0)
                    mirror[adjOf[surf]] = m;

        // ---- GEORESOURCE ----
        var geo = new List<string> { "SECTION: GEORESOURCE", "" };
        geo.Add($"World colormaps {level.ColorMaps.Count}");
        for (int i = 0; i < level.ColorMaps.Count; i++) geo.Add($"{i}:\t{level.ColorMaps[i]}");
        geo.Add("");

        geo.Add($"World vertices {vxList.Count}");
        for (int i = 0; i < vxList.Count; i++) geo.Add($"{i}: {F8(vxList[i].X)} {F8(vxList[i].Y)} {F8(vxList[i].Z)}");
        geo.Add("");

        geo.Add($"World texture vertices {txList.Count}");
        for (int i = 0; i < txList.Count; i++) geo.Add($"{i}: {F8(txList[i].X)} {F8(txList[i].Y)}");
        geo.Add("");

        geo.Add($"World adjoins {adjFlags.Count}");
        for (int i = 0; i < adjFlags.Count; i++) geo.Add($"{i}: 0x{adjFlags[i]:x} {mirror[i]} 0.00");
        geo.Add("");

        geo.Add($"World surfaces {gs}");
        foreach (var sector in level.Sectors)
            foreach (var surf in sector.Surfaces)
            {
                var sb = new StringBuilder();
                int materialIndex = surf.MaterialIndex;
                if (materials is not null && surf.Material.Length > 0)
                    for (int i = 0; i < materials.Count; i++)
                        if (materials[i].Equals(surf.Material, StringComparison.OrdinalIgnoreCase)) { materialIndex = i; break; }
                sb.Append(surfNum[surf]).Append(":\t").Append(materialIndex).Append('\t')
                  .Append("0x").Append(surf.SurfFlags.ToString("x")).Append('\t')
                  .Append("0x").Append(surf.FaceFlags.ToString("x")).Append('\t')
                  .Append(surf.Geo).Append('\t').Append(surf.Light).Append('\t').Append(surf.Tex).Append('\t')
                  .Append(adjOf[surf]).Append('\t').Append(F6(surf.ExtraLightIntensity)).Append('\t')
                  .Append(surf.Corners.Count);
                foreach (var c in surf.Corners) sb.Append('\t').Append(vertexGlobal[c.Vertex]).Append(',').Append(cornerTx[c]);
                foreach (var c in surf.Corners)
                {
                    sb.Append('\t').Append(F8(c.MonoIntensity ?? c.Intensity.R));
                    if (level.Kind == ProjectType.MysteriesOfTheSith)
                        sb.Append('\t').Append(F8(c.Intensity.R)).Append('\t').Append(F8(c.Intensity.G)).Append('\t').Append(F8(c.Intensity.B));
                }
                geo.Add(sb.ToString());
            }
        geo.Add("");
        geo.Add("#--- Surface normals ---");
        foreach (var sector in level.Sectors)
            foreach (var surf in sector.Surfaces)
            {
                surf.RecalcNormal();
                geo.Add($"{surfNum[surf]}:\t{F8(surf.Normal.X)}\t{F8(surf.Normal.Y)}\t{F8(surf.Normal.Z)}");
            }
        geo.Add("end");
        geo.Add("");

        // ---- SECTORS ----
        var sectors = new List<string> { "SECTION: SECTORS", "" };
        sectors.Add($"World sectors\t{level.Sectors.Count}");
        sectors.Add("");
        int nsurf = 0;
        for (int s = 0; s < level.Sectors.Count; s++)
        {
            var sec = level.Sectors[s];
            var box = Bounds(sec, out var center, out double radius);
            bool originalGeometry = sec.BoundsSourceVertices is not null &&
                sec.BoundsSourceVertices.SequenceEqual(sec.Vertices.Select(v => v.Position));
            if (originalGeometry)
            {
                box = sec.BoundBox ?? box;
                center = sec.Center ?? center;
                radius = sec.Radius ?? radius;
            }
            sectors.Add($"SECTOR\t{s}");
            sectors.Add($"FLAGS\t0x{sec.Flags:x}");
            sectors.Add($"AMBIENT LIGHT\t{F6(sec.Ambient.R)}");
            sectors.Add($"EXTRA LIGHT\t{F6(sec.ExtraLight.R)}");
            sectors.Add($"COLORMAP\t{ColorMapIndex(level, sec.ColorMap)}");
            sectors.Add($"TINT\t{F6(sec.Tint.R)}\t{F6(sec.Tint.G)}\t{F6(sec.Tint.B)}");
            sectors.Add($"BOUNDBOX\t{F8(box.Min.X)} {F8(box.Min.Y)} {F8(box.Min.Z)} {F8(box.Max.X)} {F8(box.Max.Y)} {F8(box.Max.Z)}");
            if (sec.HasCollideBox)
                sectors.Add($"COLLIDEBOX\t{F8(sec.CollideBox.Min.X)} {F8(sec.CollideBox.Min.Y)} {F8(sec.CollideBox.Min.Z)} {F8(sec.CollideBox.Max.X)} {F8(sec.CollideBox.Max.Y)} {F8(sec.CollideBox.Max.Z)}");
            if (sec.Thrust != Vec3.Zero)
                sectors.Add($"THRUST\t{F8(sec.Thrust.X)} {F8(sec.Thrust.Y)} {F8(sec.Thrust.Z)}");
            if (!string.IsNullOrEmpty(sec.Sound)) sectors.Add($"SOUND\t{sec.Sound} {F6(sec.SoundVolume)}");
            sectors.Add($"CENTER\t{F8(center.X)} {F8(center.Y)} {F8(center.Z)}");
            sectors.Add($"RADIUS\t{F8(radius)}");
            sectors.Add($"VERTICES\t{sec.Vertices.Count}");
            for (int v = 0; v < sec.Vertices.Count; v++) sectors.Add($"{v}: {vertexGlobal[sec.Vertices[v]]}");
            sectors.Add($"SURFACES\t{nsurf}\t{sec.Surfaces.Count}");
            nsurf += sec.Surfaces.Count;
            sectors.Add("");
        }
        sectors.Add("end");
        sectors.Add("");

        return (geo, sectors);
    }

    private static int Pool(List<Vec3> list, Dictionary<(int, Vec3), int> keys, Vertex vertex)
    {
        var key = (vertex.SourceIndex, vertex.Position);
        if (vertex.SourceIndex >= 0 && keys.TryGetValue(key, out int index)) return index;
        index = list.Count;
        list.Add(vertex.Position);
        if (vertex.SourceIndex >= 0) keys[key] = index;
        return index;
    }

    private static int PoolUv(List<Vec2> list, Dictionary<(double, double), int> keys, TexVertex uv)
    {
        var k = (uv.U, uv.V);
        if (keys.TryGetValue(k, out int idx)) return idx;
        idx = list.Count; list.Add(new Vec2(uv.U, uv.V)); keys[k] = idx; return idx;
    }

    private static int ColorMapIndex(Level level, string name)
    {
        int i = level.ColorMaps.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? 0 : i;
    }

    private static Box Bounds(Sector sector, out Vec3 center, out double radius)
    {
        var box = Box.Empty;
        foreach (var v in sector.Vertices) box.Encapsulate(v.Position);
        if (box.Max.X < box.Min.X) { box = new Box(Vec3.Zero, Vec3.Zero); }
        center = box.Center;
        double r = 0;
        foreach (var v in sector.Vertices) r = System.Math.Max(r, v.Position.DistanceTo(center));
        radius = r;
        return box;
    }

    private static string F8(double v) => double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture)
        : throw new InvalidOperationException("Geometry contains a non-finite value.");
    private static string F6(double v) => F8(v);
}

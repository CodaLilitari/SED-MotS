using System.Globalization;

namespace Sed.Formats.Keyframe;

/// <summary>
/// One keyframe entry of a KEY animation file (TKEYEntry): a rest pose at
/// <see cref="Frame"/> plus per-frame deltas, so a node's transform at frame N
/// is <c>c + d·(N − frame)</c> (U_PJKEY.PAS's GetFrame).
/// </summary>
public sealed class KeyEntry
{
    public int Frame;
    public long Flags;

    public double CX, CY, CZ, CPch, CYaw, CRol;
    public double DX, DY, DZ, DPch, DYaw, DRol;

    /// <summary>Interpolated pose at frame <paramref name="n"/> (linear, from this entry's start).</summary>
    public void GetFrame(int n, out double x, out double y, out double z,
        out double pch, out double yaw, out double rol)
    {
        int df = n - Frame;
        x = CX + DX * df;
        y = CY + DY * df;
        z = CZ + DZ * df;
        pch = CPch + DPch * df;
        yaw = CYaw + DYaw * df;
        rol = CRol + DRol * df;
    }
}

/// <summary>One animated node of a KEY file: the mesh it drives plus its keyframe entries.</summary>
public sealed class KeyNode
{
    public string MeshName = string.Empty;
    public List<KeyEntry> Entries { get; } = new();

    /// <summary>
    /// The pose at frame <paramref name="n"/>: the entry with the largest frame
    /// number ≤ n, extrapolated linearly (TKEYNode.GetFrame). Entries are in
    /// chronological order. Returns false, with zero outputs, when no entry is
    /// available at or before n. This preview policy does not extrapolate a
    /// future entry backwards or claim the engine accepts such an animation.
    /// </summary>
    public bool GetFrame(int n, out double x, out double y, out double z,
        out double pch, out double yaw, out double rol)
    {
        x = y = z = pch = yaw = rol = 0;
        if (Entries.Count == 0 || n < Entries[0].Frame) return false;
        for (int i = 1; i < Entries.Count; i++)
            if (n < Entries[i].Frame)
            {
                Entries[i - 1].GetFrame(n, out x, out y, out z, out pch, out yaw, out rol);
                return true;
            }
        // n is past the last entry — extrapolate from it (the original's index
        // bounds quirk falls back to the same entry).
        Entries[^1].GetFrame(n, out x, out y, out z, out pch, out yaw, out rol);
        return true;
    }
}

/// <summary>
/// A KEY cutscene animation file (PJKEY_IO.INC): a HEADER with the frame count
/// and FPS, plus a KEYFRAME section whose nodes each carry keyframe entries for
/// one mesh of a 3DO model.
/// </summary>
public sealed class KeyFile
{
    public string Name = string.Empty;
    public long Flags;
    public int FrameCount;
    public int Fps = 15;
    public List<KeyNode> Nodes { get; } = new();

    public static KeyFile Parse(string text)
    {
        var key = new KeyFile();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        int i = 0;
        string section = string.Empty;
        string? Next()
        {
            while (i < lines.Length)
            {
                var s = StripComment(lines[i++]).Trim().ToUpperInvariant();
                if (s.Length == 0) continue;
                if (s == "END") { section = string.Empty; return null; }
                if (s.StartsWith("SECTION:", StringComparison.Ordinal))
                {
                    section = s["SECTION:".Length..].Trim();
                    return null;
                }
                return s;
            }
            return null;
        }

        while (true)
        {
            var s = Next();
            if (s is null && section.Length == 0)
            {
                // Consumed a section header or end marker — keep scanning.
                if (i >= lines.Length && section.Length == 0) break;
                continue;
            }
            if (s is null)
            {
                if (i >= lines.Length) break;
                continue;
            }

            if (section == "HEADER")
            {
                var t = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2)
                {
                    if (t[0] == "FLAGS") key.Flags = ParseLong(t[1]);
                    else if (t[0] == "FRAMES") key.FrameCount = ParseInt(t[1]);
                    else if (t[0] == "FPS") key.Fps = ParseInt(t[1]);
                    // TYPE and JOINTS are ignored.
                }
                continue;
            }

            if (section == "KEYFRAME")
            {
                var t = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length >= 2 && t[0] == "NODES")
                {
                    int nnodes = ParseInt(t[1]);
                    for (int n = 0; n < nnodes; n++)
                    {
                        var node = new KeyNode();
                        key.Nodes.Add(node);
                        // NODE n
                        Next();
                        // MESH NAME <name>
                        var mesh = Next();
                        if (mesh is not null && mesh.StartsWith("MESH NAME", StringComparison.Ordinal))
                            node.MeshName = mesh["MESH NAME".Length..].Trim();
                        else if (mesh is not null)
                            node.MeshName = mesh; // tolerate missing keyword

                        // ENTRIES m
                        var entriesLine = Next();
                        int m = entriesLine is not null && entriesLine.StartsWith("ENTRIES", StringComparison.Ordinal)
                            ? ParseInt(entriesLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Last())
                            : 0;
                        for (int e = 0; e < m; e++)
                        {
                            var entry = new KeyEntry();
                            node.Entries.Add(entry);
                            var pose = Next();
                            if (pose is not null) ParsePose(pose, entry);
                            var delta = Next();
                            if (delta is not null) ParseDelta(delta, entry);
                        }
                    }
                }
                continue;
            }
        }

        return key;
    }

    private static void ParsePose(string s, KeyEntry entry)
    {
        // <key> <framenum> <flags:hex> <cx> <cy> <cz> <cpch> <cyaw> <crol>
        var t = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 9) return;
        entry.Frame = ParseInt(t[1]);
        entry.Flags = ParseLong(t[2]);
        entry.CX = ParseDouble(t[3]);
        entry.CY = ParseDouble(t[4]);
        entry.CZ = ParseDouble(t[5]);
        entry.CPch = ParseDouble(t[6]);
        entry.CYaw = ParseDouble(t[7]);
        entry.CRol = ParseDouble(t[8]);
    }

    private static void ParseDelta(string s, KeyEntry entry)
    {
        var t = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 6) return;
        entry.DX = ParseDouble(t[0]);
        entry.DY = ParseDouble(t[1]);
        entry.DZ = ParseDouble(t[2]);
        entry.DPch = ParseDouble(t[3]);
        entry.DYaw = ParseDouble(t[4]);
        entry.DRol = ParseDouble(t[5]);
    }

    private static string StripComment(string s)
    {
        int p = s.IndexOf('#');
        return p >= 0 ? s[..p] : s;
    }

    private static int ParseInt(string s) =>
        int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static long ParseLong(string s)
    {
        if (s.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
            return long.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    private static double ParseDouble(string s) =>
        double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}

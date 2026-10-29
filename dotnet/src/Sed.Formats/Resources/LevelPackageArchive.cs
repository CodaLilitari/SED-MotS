using System.Buffers.Binary;
using System.Text;
using Sed.Formats.Automation;
using Sed.Formats.Gob;

namespace Sed.Formats.Resources;

internal static class PackageResourcePath
{
    internal static readonly string[] Folders =
        ["mat", "3do", "3do/key", "cog", "sound", "misc/cmp", "misc/pup", "misc/snd", "misc/spr", "misc/par", "misc/ai"];

    internal static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 127 ||
            path.Any(c => c < '!' || c > '~' || ":\"<>|?*#".Contains(c)))
            throw new AgentRequestException("Archive resource paths need 1..127 printable ASCII characters without spaces or reserved characters.");
        string normalized = path.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Split('/').Any(p => p is "" or "." or ".."))
            throw new AgentRequestException("Archive resource paths must be relative, without empty, dot or parent segments.");
        return normalized;
    }

    internal static (string Name, string Folder, string Destination) Resource(string name, string folder)
    {
        if (string.IsNullOrEmpty(folder)) throw new AgentRequestException("A package resource folder is required.");
        string normalizedFolder = folder.Replace('\\', '/').ToLowerInvariant();
        if (!Folders.Contains(normalizedFolder, StringComparer.Ordinal))
            throw new AgentRequestException("Unsupported package resource folder.");
        string normalizedName = Normalize(name);
        return (normalizedName, normalizedFolder, Normalize(normalizedFolder + "/" + normalizedName));
    }
}

internal sealed record PackagePayload(string Path, byte[] Bytes);

/// <summary>Strict deterministic encoding for reviewed packages; legacy GobWriter is unchanged.</summary>
internal static class LevelPackageArchive
{
    internal static long Size(IReadOnlyList<PackagePayload> entries) =>
        checked(16L + entries.Count * 136L + entries.Sum(e => (long)e.Bytes.Length));

    internal static byte[] Build(IReadOnlyList<PackagePayload> entries, long maximumBytes)
    {
        long size = Size(entries);
        if (size > maximumBytes || size > LevelPackageOptions.HardMaxPackageBytes)
            throw new AgentRequestException("The complete package, including its directory, exceeds the output byte budget.");
        var ordered = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in ordered)
            if (PackageResourcePath.Normalize(entry.Path) != entry.Path || !seen.Add(entry.Path))
                throw new AgentRequestException("Package entries must have distinct canonical archive paths.");
        var result = new byte[checked((int)size)];
        "GOB "u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 0x14);
        int dataOffset = 12;
        var offsets = new int[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            offsets[i] = dataOffset;
            ordered[i].Bytes.CopyTo(result, dataOffset);
            dataOffset = checked(dataOffset + ordered[i].Bytes.Length);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), checked((uint)dataOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(dataOffset), checked((uint)ordered.Length));
        int row = dataOffset + 4;
        for (int i = 0; i < ordered.Length; i++, row += 136)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(row), checked((uint)offsets[i]));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(row + 4), checked((uint)ordered[i].Bytes.Length));
            Encoding.ASCII.GetBytes(ordered[i].Path.Replace('/', '\\'), result.AsSpan(row + 8, 128));
        }
        using var stream = new MemoryStream(result, writable: false);
        using var archive = new GobArchive(stream);
        if (archive.Entries.Count != ordered.Length) throw new InvalidDataException("Package directory readback changed entry count.");
        for (int i = 0; i < ordered.Length; i++)
            if (archive.Entries[i].NormalizedName != ordered[i].Path ||
                !archive.ReadBytes(archive.Entries[i]).AsSpan().SequenceEqual(ordered[i].Bytes))
                throw new InvalidDataException("Package entry did not survive exact byte readback.");
        return result;
    }
}

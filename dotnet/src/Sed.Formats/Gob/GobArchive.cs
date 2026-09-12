using System.Text;

namespace Sed.Formats.Gob;

/// <summary>One file stored inside a GOB archive.</summary>
public sealed record GobEntry(string Name, uint Offset, uint Length)
{
    /// <summary>Path normalized to forward slashes, lower-cased, for matching.</summary>
    public string NormalizedName => Name.Replace('\\', '/').ToLowerInvariant();
}

/// <summary>
/// Reader for the Jedi Knight / MotS <c>GOB</c> archive container.
///
/// Layout (little-endian): magic "GOB ", uint32 version (0x14), uint32 offset to
/// the directory; at that offset a uint32 entry count followed by that many
/// { uint32 dataOffset, uint32 length, char[128] name } records. File data blobs
/// follow the directory.
/// </summary>
public sealed class GobArchive : IDisposable
{
    private const int NameLength = 128;
    private readonly Stream _stream;
    private readonly bool _ownsStream;

    public IReadOnlyList<GobEntry> Entries { get; }
    /// <summary>Exact archive path when opened from disk; null for caller-owned streams.</summary>
    public string? SourcePath { get; private init; }

    public GobArchive(Stream stream, bool ownsStream = false)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        try { Entries = ReadDirectory(stream); }
        catch { if (ownsStream) stream.Dispose(); throw; }
    }

    public static GobArchive Open(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return new(new FileStream(fullPath, FileMode.Open, FileAccess.Read), ownsStream: true)
        { SourcePath = fullPath };
    }

    private static List<GobEntry> ReadDirectory(Stream s)
    {
        if (!s.CanSeek || !s.CanRead || s.Length < 16)
            throw new InvalidDataException("GOB input must be a readable, seekable stream with a complete header.");
        s.Seek(0, SeekOrigin.Begin);
        using var br = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);

        var magic = br.ReadBytes(4);
        if (magic.Length < 4 || magic[0] != (byte)'G' || magic[1] != (byte)'O' || magic[2] != (byte)'B' || magic[3] != (byte)' ')
            throw new InvalidDataException("Not a GOB archive (bad magic)");

        if (br.ReadUInt32() != 0x14) throw new InvalidDataException("Unsupported GOB version.");
        uint dirOffset = br.ReadUInt32();
        if (dirOffset < 12 || dirOffset > s.Length - 4)
            throw new InvalidDataException("GOB directory offset is outside the archive.");

        s.Seek(dirOffset, SeekOrigin.Begin);
        uint count = br.ReadUInt32();
        if (count > 1_000_000 || (ulong)count * (NameLength + 8UL) > (ulong)(s.Length - s.Position))
            throw new InvalidDataException("GOB directory count exceeds the available bytes or supported entry limit.");

        var entries = new List<GobEntry>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint offset = br.ReadUInt32();
            uint length = br.ReadUInt32();
            if ((ulong)offset + length > (ulong)s.Length || length > int.MaxValue)
                throw new InvalidDataException("GOB entry data is outside the archive or too large to read.");
            var nameBytes = br.ReadBytes(NameLength);
            int nul = Array.IndexOf(nameBytes, (byte)0);
            string name = Encoding.ASCII.GetString(nameBytes, 0, nul < 0 ? NameLength : nul);
            entries.Add(new GobEntry(name, offset, length));
        }
        return entries;
    }

    public IEnumerable<GobEntry> FindByExtension(string ext)
    {
        ext = ext.StartsWith('.') ? ext : "." + ext;
        return Entries.Where(e => e.NormalizedName.EndsWith(ext, StringComparison.Ordinal));
    }

    public GobEntry? Find(string nameOrSuffix)
    {
        var norm = nameOrSuffix.Replace('\\', '/').ToLowerInvariant();
        return Entries.FirstOrDefault(e =>
            e.NormalizedName == norm || e.NormalizedName.EndsWith("/" + norm, StringComparison.Ordinal)
            || Path.GetFileName(e.NormalizedName) == norm);
    }

    /// <summary>
    /// Finds an engine resource without silently choosing among ambiguous names.
    /// Prefer its standard directory, then its exact archive path, then a unique basename.
    /// Archive precedence belongs to the caller; duplicate matches are returned for refusal.
    /// </summary>
    public IReadOnlyList<GobEntry> FindResourceEntries(string name, string folder)
    {
        string normalized = name.Replace('\\', '/').ToLowerInvariant();
        string directory = folder.Replace('\\', '/').Trim('/').ToLowerInvariant();
        var matches = Entries.Where(e => e.NormalizedName == directory + "/" + normalized).ToArray();
        if (matches.Length == 0) matches = Entries.Where(e => e.NormalizedName == normalized).ToArray();
        if (matches.Length == 0) matches = Entries.Where(e => Path.GetFileName(e.NormalizedName) == normalized).ToArray();
        return matches;
    }

    public byte[] ReadBytes(GobEntry entry)
    {
        if ((ulong)entry.Offset + entry.Length > (ulong)_stream.Length || entry.Length > int.MaxValue)
            throw new InvalidDataException("GOB entry data is outside the current archive.");
        _stream.Seek(entry.Offset, SeekOrigin.Begin);
        var buffer = new byte[entry.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = _stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new EndOfStreamException("GOB entry was truncated while reading.");
            read += n;
        }
        return buffer;
    }

    /// <summary>Reads an entry as text (JKL/COG are ASCII; Latin1 preserves any stray bytes).</summary>
    public string ReadText(GobEntry entry) => Encoding.Latin1.GetString(ReadBytes(entry));

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}

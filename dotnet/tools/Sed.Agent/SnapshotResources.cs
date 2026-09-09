using System.Security.Cryptography;
using System.Text;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Material;
using Sed.Formats.ThreeDo;
using Sed.Rendering;

namespace Sed.Agent;

/// <summary>Explicit, hash-bound archive inputs for one offscreen render. Never searches installations or folders.</summary>
internal sealed class SnapshotResources : IDisposable
{
    private sealed record OpenArchive(SnapshotArchive Request, FileStream Stream, GobArchive Archive);
    private readonly List<OpenArchive> _archives = new();
    private readonly Dictionary<string, byte[]?> _bytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ThreeDoModel?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IndexedTexture?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ColormapData?> _colormaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<object> _used = new();
    private long _resolvedBytes;
    public List<string> Diagnostics { get; } = new();
    public IReadOnlyList<object> UsedResources => _used;
    public object[] Archives => _archives.Select(a => (object)new { a.Request.Path, a.Request.Sha256, bytes = a.Stream.Length }).ToArray();

    public SnapshotResources(IReadOnlyList<SnapshotArchive> requests)
    {
        try
        {
            long total = 0;
            foreach (var request in requests)
            {
                var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    total += stream.Length;
                    if (stream.Length > 536_870_912 || total > 2_147_483_648)
                        throw new AgentRequestException("Snapshot resources exceed the 512 MiB/archive or 2 GiB total budget.");
                    VerifyHash(stream, request);
                    var archive = new GobArchive(stream);
                    _archives.Add(new(request, stream, archive));
                }
                catch { stream.Dispose(); throw; }
            }
        }
        catch { Dispose(); throw; }
    }

    private static void VerifyHash(FileStream stream, SnapshotArchive request)
    {
        stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (hash != request.Sha256) throw new AgentConflictException($"Snapshot resource archive changed: {request.Path}. No output was written.");
    }
    public void VerifyUnchanged()
    {
        foreach (var archive in _archives)
        {
            VerifyHash(archive.Stream, archive.Request);
            // Also check the named path: a POSIX host may replace a file despite an open read handle.
            using var current = new FileStream(archive.Request.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            VerifyHash(current, archive.Request);
        }
    }

    private byte[]? Read(string name, string folder)
    {
        string key = folder + "/" + name;
        if (_bytes.TryGetValue(key, out var cached)) return cached;
        var normalized = name.Replace('\\', '/').ToLowerInvariant();
        foreach (var source in _archives)
        {
            // Prefer the engine's resource directory, then an exact name, then a unique basename.
            var matches = source.Archive.Entries.Where(e => e.NormalizedName == folder + "/" + normalized).ToArray();
            if (matches.Length == 0) matches = source.Archive.Entries.Where(e => e.NormalizedName == normalized).ToArray();
            if (matches.Length == 0) matches = source.Archive.Entries.Where(e => Path.GetFileName(e.NormalizedName) == normalized).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length != 1)
            {
                Diagnostics.Add($"Ambiguous {folder} resource '{name}' in {source.Request.Path}; not resolved.");
                return _bytes[key] = null;
            }
            var entry = matches[0];
            if (entry.Length > 134_217_728 || _resolvedBytes + entry.Length > 268_435_456)
                throw new AgentRequestException("Resolved snapshot resources exceed the 128 MiB/entry or 256 MiB total budget.");
            var bytes = source.Archive.ReadBytes(entry);
            _resolvedBytes += bytes.Length;
            _used.Add(new { requested = name, archive = source.Request.Path, entry = entry.Name, bytes = bytes.Length, sha256 = AgentWorkspace.Hash(bytes) });
            return _bytes[key] = bytes;
        }
        Diagnostics.Add($"Missing {folder} resource '{name}' in the explicit archives.");
        return _bytes[key] = null;
    }
    public IndexedTexture? Texture(string name)
    {
        if (_textures.TryGetValue(name, out var cached)) return cached;
        try
        {
            var bytes = Read(name, "mat");
            if (bytes is null) return _textures[name] = null;
            var mat = MatFile.Read(bytes);
            return _textures[name] = mat.IsColor ? new IndexedTexture(1, 1, [(byte)mat.ColorIndex], IsSolid: true) : new(mat.Width, mat.Height, mat.Indices);
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or OverflowException)
        { Diagnostics.Add($"MAT '{name}': {error.Message}"); return _textures[name] = null; }
    }
    public ColormapData? Colormap(string name)
    {
        if (_colormaps.TryGetValue(name, out var cached)) return cached;
        try
        {
            var bytes = Read(name, "misc/cmp");
            if (bytes is null) return _colormaps[name] = null;
            var cmp = Sed.Formats.Material.Colormap.Read(bytes);
            if (bytes.Length < 64 + 768 + 16384) Diagnostics.Add($"CMP '{name}' omits its light ramp; using an identity ramp.");
            return _colormaps[name] = new ColormapData(cmp.PaletteRgb, cmp.LightTable);
        }
        catch (Exception error) when (error is IOException or ArgumentException)
        { Diagnostics.Add($"CMP '{name}': {error.Message}"); return _colormaps[name] = null; }
    }
    public ThreeDoModel? Model(string name)
    {
        if (_models.TryGetValue(name, out var cached)) return cached;
        try
        {
            var bytes = Read(name, "3do");
            if (bytes is null) return _models[name] = null;
            var model = ThreeDoParser.Parse(Encoding.Latin1.GetString(bytes));
            if (model.Version is not (2.1 or 2.2) || model.Meshes.Count == 0
                || !model.Meshes.Any(m => m.Faces.Any(f => f.VertexIndices.Count >= 3)))
                throw new FormatException("Expected a drawable JK/MotS 3DO 2.1 or 2.2 model; content is empty, incomplete or unsupported.");
            return _models[name] = model;
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or OverflowException)
        { Diagnostics.Add($"3DO '{name}': {error.Message}"); return _models[name] = null; }
    }
    public void Dispose()
    {
        foreach (var archive in _archives) { archive.Archive.Dispose(); archive.Stream.Dispose(); }
        _archives.Clear();
    }
}

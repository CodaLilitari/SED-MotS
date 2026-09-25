using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.ThreeDo;

namespace Sed.Formats.Resources;

public record ResourceArchiveRequest(string Path, string Sha256);
public sealed record ResourceArchiveReceipt(string Path, string Sha256, long Bytes);
public sealed record ResourceEntryReceipt(string Requested, string Archive, string Entry, long Bytes, string Sha256);
public sealed record ResourceReadRequest(string Name, string Folder);
public enum ResourceLookupStatus { Resolved, Missing, Ambiguous }

/// <summary>Captured model resources for one validation/export, with an immediate pre-write unchanged check.</summary>
public interface IArchitectureResourceProvider
{
    ResolvedArchitectureModel? ResolveArchitectureModel(string name);
    void VerifyUnchanged();
    string ResourceRevision { get; }
    IReadOnlyList<ResourceArchiveReceipt> Archives { get; }
    IReadOnlyList<ResourceEntryReceipt> UsedResources { get; }
    IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>Explicit hash-bound archives. No installation discovery or directory enumeration.</summary>
public sealed class ExplicitArchiveResources : IArchitectureResourceProvider, IDisposable
{
    private sealed record OpenArchive(ResourceArchiveRequest Request, FileStream Stream, GobArchive Archive);
    private sealed record CapturedEntry(byte[] Bytes, ResourceEntryReceipt Receipt);
    private readonly List<OpenArchive> _archives = new();
    private readonly Dictionary<string, CapturedEntry?> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceLookupStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResolvedArchitectureModel?> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceEntryReceipt> _used = new();
    private readonly List<ResourceReadRequest> _requests = new();
    private readonly List<string> _diagnostics = new();
    private long _resolvedBytes;
    private bool _disposed;
    public IReadOnlyList<string> Diagnostics => _diagnostics.AsReadOnly();
    public IReadOnlyList<ResourceEntryReceipt> UsedResources => _used.AsReadOnly();
    /// <summary>Exact resolution requests, including misses, for pre-write replay.</summary>
    public IReadOnlyList<ResourceReadRequest> ReadRequests => _requests.AsReadOnly();
    public IReadOnlyList<ResourceArchiveReceipt> Archives => Array.AsReadOnly(_archives.Select(a =>
        new ResourceArchiveReceipt(a.Request.Path, a.Request.Sha256, a.Stream.Length)).ToArray());
    public string ResourceRevision => ComputeRevision(Archives, UsedResources);

    public static string ComputeRevision(IReadOnlyList<ResourceArchiveReceipt> archives, IReadOnlyList<ResourceEntryReceipt> entries)
    {
        // Archive order is significant; resource request order is not. Version binds resolution and ARCH validation semantics.
        var canonical = new { version = "sed-architecture-resources-v1", validatorVersion = ArchitectureBindingValidator.Version, archives,
            entries = entries.OrderBy(e => e.Requested, StringComparer.Ordinal).ThenBy(e => e.Archive, StringComparer.Ordinal)
                .ThenBy(e => e.Entry, StringComparer.Ordinal).ToArray() };
        return AgentWorkspace.Hash(JsonSerializer.SerializeToUtf8Bytes(canonical));
    }

    public ExplicitArchiveResources(IReadOnlyList<ResourceArchiveRequest> requests)
    {
        if (requests.Count is < 1 or > 16) throw new AgentRequestException("resourceArchives must contain 1 through 16 explicit archives.");
        try
        {
            long total = 0;
            var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var supplied in requests)
            {
                if (string.IsNullOrWhiteSpace(supplied.Path) || !Path.IsPathFullyQualified(supplied.Path))
                    throw new AgentRequestException("Resource archive paths must be explicit absolute paths.");
                var request = new ResourceArchiveRequest(Path.GetFullPath(supplied.Path), ResourceManifest.NormalizeHash(supplied.Sha256, "resource archive sha256"));
                if (!paths.Add(request.Path)) throw new AgentRequestException("Duplicate resource archive path.");
                var stream = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                try
                {
                    total += stream.Length;
                    if (stream.Length > 536_870_912 || total > 2_147_483_648)
                        throw new AgentRequestException("Resources exceed the 512 MiB/archive or 2 GiB total budget.");
                    VerifyHash(stream, request);
                    _archives.Add(new(request, stream, new GobArchive(stream)));
                }
                catch { stream.Dispose(); throw; }
            }
        }
        catch { Dispose(); throw; }
    }

    private static void VerifyHash(FileStream stream, ResourceArchiveRequest request)
    {
        stream.Position = 0;
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException($"Resource archive changed: {request.Path}. No output was written.");
    }

    public void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var archive in _archives)
        {
            VerifyHash(archive.Stream, archive.Request);
            // Also check named paths: POSIX permits replacement despite an open read handle.
            try
            {
                using var current = new FileStream(archive.Request.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                VerifyHash(current, archive.Request);
            }
            catch (FileNotFoundException) { throw new AgentConflictException($"Resource archive disappeared: {archive.Request.Path}. No output was written."); }
        }
    }

    public ReadOnlyMemory<byte>? Read(string name, string folder)
    {
        var entry = Capture(name, folder);
        if (entry is null) return null;
        return new ReadOnlyMemory<byte>(entry.Bytes);
    }

    /// <summary>Exact captured receipt for the same normalized read request, including cached reads.</summary>
    public ResourceEntryReceipt? ReceiptFor(string name, string folder) => Capture(name, folder)?.Receipt;

    /// <summary>Structured result of the same captured lookup; ambiguity never falls through to another archive.</summary>
    public ResourceLookupStatus StatusFor(string name, string folder)
    {
        Capture(name, folder);
        return _statuses[RequestKey(name, folder)];
    }

    private static string RequestKey(string name, string folder) =>
        folder.Replace('\\', '/').Trim('/').ToLowerInvariant() + "/" + name.Replace('\\', '/').ToLowerInvariant();

    private CapturedEntry? Capture(string name, string folder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        folder = folder.Replace('\\', '/').Trim('/').ToLowerInvariant();
        string key = RequestKey(name, folder);
        if (_entries.TryGetValue(key, out var cached)) return cached;
        _requests.Add(new(name, folder));
        foreach (var source in _archives)
        {
            var matches = source.Archive.FindResourceEntries(name, folder);
            if (matches.Count == 0) continue;
            if (matches.Count != 1)
            {
                _diagnostics.Add($"Ambiguous {folder} resource '{name}' in {source.Request.Path}; not resolved.");
                _statuses[key] = ResourceLookupStatus.Ambiguous;
                return _entries[key] = null;
            }
            var entry = matches[0];
            if (entry.Length > 134_217_728 || _resolvedBytes + entry.Length > 268_435_456)
                throw new AgentRequestException("Resolved resources exceed the 128 MiB/entry or 256 MiB total budget.");
            var bytes = source.Archive.ReadBytes(entry);
            _resolvedBytes += bytes.Length;
            var receipt = new ResourceEntryReceipt(name, source.Request.Path, entry.Name, bytes.LongLength, AgentWorkspace.Hash(bytes));
            _used.Add(receipt);
            _statuses[key] = ResourceLookupStatus.Resolved;
            return _entries[key] = new(bytes, receipt);
        }
        _diagnostics.Add($"Missing {folder} resource '{name}' in the explicit archives.");
        _statuses[key] = ResourceLookupStatus.Missing;
        return _entries[key] = null;
    }

    public ResolvedArchitectureModel? ResolveArchitectureModel(string name)
    {
        if (_models.TryGetValue(name, out var cached)) return cached;
        try
        {
            var entry = Capture(name, "3do");
            if (entry is null) return _models[name] = null;
            var topology = ThreeDoTopologyReader.Read(Encoding.Latin1.GetString(entry.Bytes));
            return _models[name] = new(name, entry.Receipt.Archive + "::" + entry.Receipt.Entry, entry.Receipt.Sha256, topology);
        }
        catch (Exception error) when (error is IOException or FormatException or ArgumentException or OverflowException)
        { _diagnostics.Add($"3DO '{name}': {error.Message}"); return _models[name] = null; }
    }

    public void Dispose()
    {
        foreach (var archive in _archives) { archive.Archive.Dispose(); archive.Stream.Dispose(); }
        _archives.Clear();
        _disposed = true;
    }
}

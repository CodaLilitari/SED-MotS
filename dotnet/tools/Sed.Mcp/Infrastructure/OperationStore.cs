using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sed.Core.IO;
using Sed.Formats.Automation;

namespace Sed.Mcp.Infrastructure;

public enum OperationState { Running, Committed, Failed, Unknown }

public sealed record OperationIntent(string Command, string? InputPath, string? InputSha256,
    string? SourceRevision, string OutputPath, string RequestSha256, string ExpectedOutputSha256);

public sealed record OperationReceipt(string OperationId, OperationIntent Intent, OperationState State,
    DateTimeOffset BeganUtc, DateTimeOffset UpdatedUtc, string? Message);

public sealed record OperationBeginResult(OperationReceipt Receipt, bool Created);

/// <summary>Historical state and current file evidence are independent. Matching bytes never imply a commit.</summary>
public sealed record OperationObservation(OperationReceipt Receipt, OperationState ObservedState, bool OriginRunning,
    bool? OutputExists, string? ObservedOutputSha256, bool? MatchesExpectedOutput, string? OutputInspectionError);

/// <summary>
/// A local durable journal, not an exactly-once executor or an OS isolation boundary.
/// Begin must succeed before output creation/promotion. Complete is called only
/// after the creator successfully promotes its output. A lost running owner is
/// observed as unknown; file hashes are evidence, never a replacement history.
/// </summary>
public sealed class OperationStore
{
    private const int MaxReceiptBytes = 64 * 1024;
    private const long MaxObservedOutputBytes = AgentWorkspace.MaxLevelBytes;
    // Operation IDs are caller supplied and long lived on disk. Fixed stripes
    // serialize same-receipt calls without retaining a lock for every ID ever seen.
    private static readonly object[] Gates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };
    private sealed record StoredOperation(int SchemaVersion, OperationReceipt Receipt, string Lease);
    private readonly McpFilePolicy _policy;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, string> _owned = new(StringComparer.Ordinal);
    internal int ActiveLeaseCount => _owned.Count;

    public OperationStore(McpFilePolicy policy)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _directory = policy.ReceiptDirectory();
    }

    public OperationBeginResult Begin(string operationId, OperationIntent intent)
    {
        string id = NormalizeId(operationId);
        var normalized = NormalizeIntent(intent);
        string path = ReceiptPath(id);
        lock (Gate(path))
        {
            var existing = Read(path);
            if (existing is not null) return Existing(existing, normalized);
            // An unrelated existing output cannot become a new operation merely
            // because its bytes match a caller's expected hash.
            _policy.WriteOutput(normalized.OutputPath, Path.GetExtension(normalized.OutputPath));
            var now = DateTimeOffset.UtcNow;
            var receipt = new OperationReceipt(id, normalized, OperationState.Running, now, now, null);
            string lease = Guid.NewGuid().ToString("N");
            var record = new StoredOperation(1, receipt, lease);
            if (!Write(record, path, replace: false))
                return Existing(Read(path) ?? throw new InvalidOperationException("Concurrent operation receipt disappeared."), normalized);
            _owned[id] = lease;
            return new(receipt, true);
        }
    }

    public OperationReceipt Complete(string operationId, string? message = null) => Finish(operationId, OperationState.Committed, message);
    public OperationReceipt Fail(string operationId, string message) => Finish(operationId, OperationState.Failed, message);
    public OperationReceipt MarkUnknown(string operationId, string message) => Finish(operationId, OperationState.Unknown, message);

    public OperationObservation? Get(string operationId)
    {
        string id = NormalizeId(operationId), path = ReceiptPath(id);
        lock (Gate(path))
        {
            var stored = Read(path);
            return stored is null ? null : Observe(stored);
        }
    }

    private OperationReceipt Finish(string operationId, OperationState state, string? message)
    {
        if (message?.Length > 4096) throw new AgentRequestException("Operation message exceeds 4096 characters.");
        string id = NormalizeId(operationId), path = ReceiptPath(id);
        lock (Gate(path))
        {
            var stored = Read(path) ?? throw new AgentRequestException("Operation does not exist; Begin must precede completion.");
            if (stored.Receipt.State != OperationState.Running)
            {
                // Repeating an already-recorded terminal state is a read, not
                // authority to amend it. No caller can change its message/time.
                if (stored.Receipt.State == state) return stored.Receipt;
                throw new AgentConflictException("The operation already has a terminal outcome; it cannot be replaced.");
            }
            if (!_owned.TryGetValue(id, out string? lease) || lease != stored.Lease)
                throw new AgentConflictException("Only the store instance that created this operation may record its outcome. Inspect its receipt; do not replay it.");
            if (state == OperationState.Committed)
            {
                var evidence = Observe(stored);
                if (evidence.MatchesExpectedOutput != true)
                    throw new AgentConflictException("The output is absent, unreadable or differs from expectedOutputSha256. Record unknown/failed as appropriate; do not claim committed.");
            }
            var now = DateTimeOffset.UtcNow;
            // Wall-clock corrections must not make our own next read reject a
            // receipt merely because the timestamp moved backwards.
            var receipt = stored.Receipt with { State = state,
                UpdatedUtc = now < stored.Receipt.UpdatedUtc ? stored.Receipt.UpdatedUtc : now, Message = message };
            Write(stored with { Receipt = receipt }, path, replace: true);
            _owned.TryRemove(id, out _);
            return receipt;
        }
    }

    private OperationObservation Observe(StoredOperation stored)
    {
        var receipt = stored.Receipt;
        bool originRunning = receipt.State == OperationState.Running;
        var observedState = originRunning && (!_owned.TryGetValue(receipt.OperationId, out string? lease) || lease != stored.Lease)
            ? OperationState.Unknown : receipt.State;
        try
        {
            string path = _policy.ResolveOutput(receipt.Intent.OutputPath, Path.GetExtension(receipt.Intent.OutputPath), allowExisting: true);
            if (!File.Exists(path)) return new(receipt, observedState, originRunning, false, null, false, null);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length > MaxObservedOutputBytes)
                return new(receipt, observedState, originRunning, true, null, null, "Output exceeds the 128 MiB observation limit.");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            for (long remaining = length; remaining > 0;)
            {
                int count = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count == 0) throw new IOException("Output shrank during observation.");
                digest.AppendData(buffer, 0, count);
                remaining -= count;
            }
            if (stream.ReadByte() != -1) throw new IOException("Output grew during observation.");
            string hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            return new(receipt, observedState, originRunning, true, hash, hash == receipt.Intent.ExpectedOutputSha256, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or AgentRequestException)
        {
            return new(receipt, observedState, originRunning, null, null, null, "Output could not be safely inspected: " + error.Message);
        }
    }

    private OperationIntent NormalizeIntent(OperationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateIntentFields(intent);
        string? input = intent.InputPath is null ? null : _policy.ResolveIntentInput(intent.InputPath);
        string output = _policy.ResolveOutput(intent.OutputPath, Path.GetExtension(intent.OutputPath), allowExisting: true);
        if (input is not null && PathEqual(input, output)) throw new AgentRequestException("Operation output must not alias its input.");
        return intent with { InputPath = input, OutputPath = output,
            InputSha256 = intent.InputSha256 is null ? null : Hash(intent.InputSha256, "inputSha256"),
            SourceRevision = intent.SourceRevision is null ? null : Hash(intent.SourceRevision, "sourceRevision"),
            RequestSha256 = Hash(intent.RequestSha256, "requestSha256"),
            ExpectedOutputSha256 = Hash(intent.ExpectedOutputSha256, "expectedOutputSha256") };
    }

    private static void ValidateIntentFields(OperationIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.Command) || intent.Command.Length > 64 || intent.Command.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new AgentRequestException("Operation command must be a bounded command token.");
        if (intent.InputPath is null && (intent.InputSha256 is not null || intent.SourceRevision is not null))
            throw new AgentRequestException("Input hashes require an input path.");
        if (intent.InputPath is not null && intent.InputSha256 is null)
            throw new AgentRequestException("An input path requires inputSha256.");
        if (string.IsNullOrWhiteSpace(intent.OutputPath)) throw new AgentRequestException("Operation requires an output path.");
        if (intent.InputSha256 is not null) Hash(intent.InputSha256, "inputSha256");
        if (intent.SourceRevision is not null) Hash(intent.SourceRevision, "sourceRevision");
        Hash(intent.RequestSha256, "requestSha256");
        Hash(intent.ExpectedOutputSha256, "expectedOutputSha256");
    }

    private static OperationBeginResult Existing(StoredOperation existing, OperationIntent intent)
    {
        var before = existing.Receipt.Intent;
        if (before.Command != intent.Command || !PathEqual(before.InputPath, intent.InputPath) || !PathEqual(before.OutputPath, intent.OutputPath) ||
            before.InputSha256 != intent.InputSha256 || before.SourceRevision != intent.SourceRevision ||
            before.RequestSha256 != intent.RequestSha256 || before.ExpectedOutputSha256 != intent.ExpectedOutputSha256)
            throw new AgentConflictException("operationId is already bound to different immutable intent; use a new ID for a different operation.");
        return new(existing.Receipt, false);
    }

    private string ReceiptPath(string id)
    {
        string path = Path.Combine(_directory, id + ".json");
        _policy.CheckReceiptPath(path);
        return path;
    }

    private StoredOperation? Read(string path)
    {
        _policy.CheckReceiptPath(path);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long length = stream.Length;
            if (length > MaxReceiptBytes) throw new InvalidOperationException("Operation receipt exceeds its size limit.");
            byte[] bytes = new byte[checked((int)length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidOperationException("Operation receipt grew during inspection; outcome is untrusted.");
            using var document = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(document.RootElement);
            var result = document.RootElement.Deserialize<StoredOperation>(Json);
            if (result is null || result.SchemaVersion != 1 || result.Receipt is null || result.Receipt.Intent is null ||
                result.Receipt.OperationId != Path.GetFileNameWithoutExtension(path) ||
                !Guid.TryParseExact(result.Lease, "N", out _) || !Enum.IsDefined(result.Receipt.State) ||
                result.Receipt.BeganUtc == default || result.Receipt.UpdatedUtc < result.Receipt.BeganUtc ||
                result.Receipt.Message?.Length > 4096)
                throw new InvalidOperationException("Operation receipt is malformed or has unsupported schema.");
            // Validate structure before inspecting outputs, but do not require
            // historic inputs to still exist. A moved/deleted input must not erase
            // a completed operation's receipt or turn missing output evidence into
            // a fabricated history.
            ValidateIntentFields(result.Receipt.Intent);
            if (!Path.IsPathFullyQualified(result.Receipt.Intent.OutputPath) ||
                (result.Receipt.Intent.InputPath is string input && !Path.IsPathFullyQualified(input)) ||
                PathEqual(result.Receipt.Intent.InputPath, result.Receipt.Intent.OutputPath))
                throw new InvalidOperationException("Operation receipt paths are malformed.");
            return result;
        }
        catch (Exception error) when (error is JsonException or AgentRequestException)
        { throw new InvalidOperationException("Operation receipt is unreadable; outcome is untrusted. Preserve the receipt for diagnosis.", error); }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Operation receipt contains duplicate JSON properties.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    private bool Write(StoredOperation stored, string path, bool replace)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(stored, Json);
        if (bytes.Length > MaxReceiptBytes) throw new AgentRequestException("Operation receipt exceeds its size limit.");
        string temporary = Path.Combine(_directory, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
        _policy.CheckReceiptPath(temporary);
        bool promoted = false, temporaryCreated = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            _policy.CheckReceiptPath(path);
            try
            {
                if (replace) File.Move(temporary, path, overwrite: true);
                else AtomicFilePromotion.MoveNew(temporary, path);
                promoted = true;
                return true;
            }
            catch (IOException) when (!replace && File.Exists(path)) { return false; }
        }
        finally
        {
            // Only this invocation's generated temporary file may be removed.
            // If paths changed into reparse points, leave it for diagnosis.
            if (temporaryCreated && !promoted)
            {
                try { _policy.CheckReceiptPath(temporary); File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or AgentRequestException) { }
            }
        }
    }

    private static string NormalizeId(string id)
    {
        if (id is null || id != id.Trim() || !(Guid.TryParseExact(id, "N", out var guid) || Guid.TryParseExact(id, "D", out guid)))
            throw new AgentRequestException("operationId must be a GUID in N or D format.");
        return guid.ToString("N");
    }
    private static string Hash(string value, string name)
    {
        if (value is null || value.Length != 64 || value.Any(c => !Uri.IsHexDigit(c)))
            throw new AgentRequestException(name + " must contain exactly 64 hexadecimal SHA256 characters.");
        return value.ToLowerInvariant();
    }
    private static bool PathEqual(string? first, string? second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static object Gate(string path) => Gates[(uint)PathComparer.GetHashCode(path) % (uint)Gates.Length];
}

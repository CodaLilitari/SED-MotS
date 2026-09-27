using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

[assembly: InternalsVisibleTo("Sed.Mcp.Tests")]

namespace Sed.Mcp.Infrastructure;

public sealed record SnapshotOutputState(string Path, string Status, bool ExistedBefore,
    long? Bytes = null, string? Sha256 = null, string? Detail = null, bool ProcessExitConfirmed = true);

public sealed record SnapshotWorkerResult(JsonElement Receipt, byte[] ImageBytes, string Sha256,
    SnapshotOutputState OutputState, string StandardError = "");

public sealed class SnapshotWorkerException : Exception
{
    public string Code { get; }
    public bool IsError => true;
    public bool IsHostFault { get; }
    public int? ExitCode { get; }
    public JsonElement? Receipt { get; }
    public SnapshotOutputState OutputState { get; }
    public string StandardError { get; }

    internal SnapshotWorkerException(string code, string message, bool isHostFault, SnapshotOutputState outputState,
        int? exitCode = null, JsonElement? receipt = null, string standardError = "", Exception? inner = null)
        : base(message, inner)
    {
        Code = code; IsHostFault = isHostFault; OutputState = outputState;
        ExitCode = exitCode; Receipt = receipt; StandardError = standardError;
    }
}

/// <summary>
/// Runs only the host-configured sed-agent, with no shell or visible window.
/// The caller validates input/output workspace policy before calling. Identity
/// includes the executable and five managed companions, checked around process
/// execution; this is not a full package manifest or a filesystem race lock.
/// Error output state is an observation, never an assertion that this worker
/// created an existing file. No output file is deleted on failure/cancellation.
/// Cleanup can add two bounded ten-second waits after the execution deadline.
/// If child termination cannot be confirmed, this instance admits no more work.
/// </summary>
public sealed class SnapshotWorker
{
    public const int MaxRequestBytes = 1024 * 1024;
    public const int MaxPipeBytes = 2 * 1024 * 1024 - 1;
    public const int MaxImageBytes = 16 * 1024 * 1024;
    private const long MaxIdentityComponentBytes = 512L * 1024 * 1024;
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Companions =
        ["sed-agent.dll", "Sed.Core.dll", "Sed.Formats.dll", "Sed.Rendering.dll", "Sed.Rendering.Vulkan.dll"];
    private readonly string _agentPath;
    private readonly string _expectedVersion;
    private readonly TimeSpan _timeout;
    private readonly ISnapshotProcessFactory _processFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _verifiedIdentity;
    private bool _terminationUnconfirmed;

    public SnapshotWorker(string agentPath, string expectedVersion, TimeSpan? timeout = null)
        : this(agentPath, expectedVersion, timeout, new SnapshotProcessFactory()) { }

    internal SnapshotWorker(string agentPath, string expectedVersion, TimeSpan? timeout, ISnapshotProcessFactory processFactory)
    {
        if (!Path.IsPathFullyQualified(agentPath)) throw new ArgumentException("The configured agent executable must be an absolute path.", nameof(agentPath));
        if (string.IsNullOrWhiteSpace(expectedVersion)) throw new ArgumentException("An exact expected agent informational version is required.", nameof(expectedVersion));
        _agentPath = Path.GetFullPath(agentPath); _expectedVersion = expectedVersion;
        _timeout = timeout ?? MaximumTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > MaximumTimeout) throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive and at most 180 seconds.");
        _processFactory = processFactory;
    }

    public async Task<SnapshotWorkerResult> RunAsync(string levelPath, string requestJson, string outputPath, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(levelPath) || !Path.IsPathFullyQualified(outputPath))
            throw new ArgumentException("Snapshot input and output paths must be caller-validated absolute paths.");
        levelPath = Path.GetFullPath(levelPath); outputPath = Path.GetFullPath(outputPath);
        // Observe before the first cancellation point, so a cancelled queued call
        // does not describe a pre-existing file as newly appeared.
        bool entered = false, existedBefore = ObserveOutput(outputPath, false).Status != "missing";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_timeout);
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            if (_terminationUnconfirmed)
                throw Fault("snapshot_worker_unavailable", "An earlier worker shutdown could not be confirmed; no new worker was started. Resolve that process before replacing this host.",
                    true, exitConfirmed: false);
            var initial = ObserveOutput(outputPath, false);
            existedBefore = initial.Status != "missing";
            if (existedBefore)
                throw Fault("snapshot_output_exists", "Snapshot output already exists or cannot be inspected; no worker was started.", false);
            byte[] input;
            try
            {
                if (StrictUtf8.GetByteCount(requestJson) > MaxRequestBytes)
                    throw Fault("snapshot_invalid_request", "Snapshot request exceeds 1 MiB; no worker was started.", false);
                input = StrictUtf8.GetBytes(requestJson);
            }
            catch (EncoderFallbackException error) { throw Fault("snapshot_invalid_request", "Snapshot JSON contains invalid Unicode.", false, inner: error); }

            var identity = await ReadIdentityAsync(deadline.Token).ConfigureAwait(false);
            if (_verifiedIdentity is null)
            {
                var version = await ExecuteAsync(["--version"], Array.Empty<byte>(), deadline.Token).ConfigureAwait(false);
                var receipt = ParseReceipt(version.StandardOutput);
                if (version.ExitCode != 0 || !Integer(receipt, "schemaVersion", 1) || !Text(receipt, "name", "sed-agent")
                    || !Text(receipt, "version", _expectedVersion))
                    throw Fault("snapshot_version_mismatch", "Configured sed-agent did not report the required name, schema and exact informational version.",
                        true, version.ExitCode, receipt, version.StandardError);
                RequireIdentity(identity, await ReadIdentityAsync(deadline.Token).ConfigureAwait(false));
                _verifiedIdentity = identity;
            }
            else RequireIdentity(_verifiedIdentity, identity);
            deadline.Token.ThrowIfCancellationRequested();
            var run = await ExecuteAsync(["snapshot", levelPath, "-", outputPath], input, deadline.Token).ConfigureAwait(false);
            RequireIdentity(_verifiedIdentity, await ReadIdentityAsync(deadline.Token).ConfigureAwait(false));
            var result = ParseReceipt(run.StandardOutput);
            if (run.ExitCode != 0)
            {
                string? domainCode = run.ExitCode switch { 2 => "invalid_request", 3 => "validation_failed", 4 => "conflict", 5 => "io_error", _ => null };
                if (domainCode is not null && Integer(result, "schemaVersion", 1) && Text(result, "error", domainCode)
                    && result.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                    throw Fault(domainCode, message.GetString()!, false, run.ExitCode, result, run.StandardError);
                throw Fault("snapshot_protocol_error", "Snapshot worker exited without a matching CLI domain-error receipt.", true, run.ExitCode, result, run.StandardError);
            }
            if (!Integer(result, "schemaVersion", 1) || result.TryGetProperty("error", out _)
                || !result.TryGetProperty("written", out var written) || written.ValueKind != JsonValueKind.String
                || !SameAbsolutePath(written.GetString()!, outputPath)
                || !result.TryGetProperty("outputBytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt64(out long expectedBytes)
                || expectedBytes is < 8 or > MaxImageBytes
                || !result.TryGetProperty("outputSha256", out var hash) || hash.ValueKind != JsonValueKind.String
                || !ValidHash(hash.GetString()))
                throw Fault("snapshot_protocol_error", "Snapshot success receipt has an invalid output path, byte count or hash.", true, run.ExitCode, result, run.StandardError);

            // Never read the receipt's path: only the caller's expected path.
            RejectReparseOutput(outputPath);
            byte[] image;
            await using (var stream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true))
            {
                if (stream.Length != expectedBytes || stream.Length > MaxImageBytes)
                    throw Fault("snapshot_output_mismatch", "Snapshot file length differs from the worker receipt.", true, run.ExitCode, result, run.StandardError);
                image = await ReadBoundedAsync(stream, MaxImageBytes, "snapshot_image_too_large", deadline.Token).ConfigureAwait(false);
            }
            string actualHash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
            if (image.LongLength != expectedBytes || !image.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                || !actualHash.Equals(hash.GetString(), StringComparison.OrdinalIgnoreCase))
                throw Fault("snapshot_output_mismatch", "Snapshot PNG signature, bytes or SHA256 differs from the worker receipt.", true, run.ExitCode, result, run.StandardError);
            RejectReparseOutput(outputPath);
            return new(result, image, actualHash, new(outputPath, "file", false, image.LongLength, actualHash), run.StandardError);
        }
        catch (SnapshotWorkerException) { throw; }
        catch (OperationCanceledException error)
        { throw Fault(token.IsCancellationRequested ? "snapshot_cancelled" : "snapshot_timeout",
            token.IsCancellationRequested ? "Snapshot was cancelled; inspect outputState for any file already promoted." : "Snapshot exceeded its process deadline; inspect outputState for any file already promoted.", true, inner: error); }
        catch (ProcessFault error)
        {
            if (!error.ExitConfirmed) _terminationUnconfirmed = true;
            throw Fault(error.Code, error.Message, true, inner: error, exitConfirmed: error.ExitConfirmed);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException or DecoderFallbackException)
        { throw Fault("snapshot_host_error", error.Message, true, inner: error); }
        finally { if (entered) _gate.Release(); }

        SnapshotWorkerException Fault(string code, string message, bool host, int? exitCode = null,
            JsonElement? receipt = null, string stderr = "", Exception? inner = null, bool exitConfirmed = true) =>
            new(code, message, host, ObserveOutput(outputPath, existedBefore) with { ProcessExitConfirmed = exitConfirmed }, exitCode, receipt, stderr, inner);
    }

    private async Task<Dictionary<string, string>> ReadIdentityAsync(CancellationToken token)
    {
        var identity = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in new[] { _agentPath }.Concat(Companions.Select(name => Path.Combine(Path.GetDirectoryName(_agentPath)!, name))))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ProcessFault("snapshot_identity_error", "Configured worker executable or managed companion is a reparse point.");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            long length = stream.Length;
            if (length > MaxIdentityComponentBytes) throw new ProcessFault("snapshot_identity_error", "A configured worker component exceeds the 512 MiB identity budget.");
            try { identity[path] = await HashCapturedLengthAsync(stream, length, token).ConfigureAwait(false); }
            catch (IOException error)
            { throw new ProcessFault("snapshot_identity_changed", "Configured worker component could not be hashed at its captured size; restart with the verified package.", inner: error); }
        }
        return identity;
    }

    // Consume only the captured byte count plus one EOF probe. A growing file
    // cannot expand this read budget; the execution deadline still bounds waits.
    internal static async Task<string> HashCapturedLengthAsync(Stream stream, long capturedLength, CancellationToken token)
    {
        if (capturedLength is < 0 or > MaxIdentityComponentBytes)
            throw new ArgumentOutOfRangeException(nameof(capturedLength));
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        for (long remaining = capturedLength; remaining > 0;)
        {
            token.ThrowIfCancellationRequested();
            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (count == 0) throw new IOException("Worker component shrank during identity verification.");
            digest.AppendData(buffer, 0, count);
            remaining -= count;
        }
        token.ThrowIfCancellationRequested();
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0 || stream.Length != capturedLength)
            throw new IOException("Worker component changed size during identity verification.");
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static void RequireIdentity(Dictionary<string, string> expected, Dictionary<string, string> actual)
    {
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out string? hash) || hash != pair.Value))
            throw new ProcessFault("snapshot_identity_changed", "Configured worker executable or managed companion changed; restart with the verified package.");
    }

    private async Task<ProcessResult> ExecuteAsync(string[] arguments, byte[] input, CancellationToken token)
    {
        var start = new ProcessStartInfo(_agentPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_agentPath)!,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var child = _processFactory.Start(start);
        using var stopIo = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stdout = ReadBoundedAsync(child.StandardOutput, MaxPipeBytes, "snapshot_stdout_limit", stopIo.Token);
        var stderr = ReadBoundedAsync(child.StandardError, MaxPipeBytes, "snapshot_stderr_limit", stopIo.Token);
        var stdin = WriteInputAsync(child.StandardInput, input, stopIo.Token);
        var exited = child.WaitForExitAsync(stopIo.Token);
        Task[] all = [stdout, stderr, stdin, exited];
        var pending = all.ToList();
        try
        {
            while (pending.Count != 0)
            {
                Task completed = await Task.WhenAny(pending).WaitAsync(token).ConfigureAwait(false);
                await completed.ConfigureAwait(false); pending.Remove(completed);
            }
            return new(child.ExitCode, await stdout.ConfigureAwait(false), Encoding.UTF8.GetString(await stderr.ConfigureAwait(false)));
        }
        catch (Exception error)
        {
            stopIo.Cancel();
            bool exitConfirmed = false;
            try { child.KillTree(); } catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            try { await child.WaitForExitAsync(CancellationToken.None).WaitAsync(CleanupTimeout).ConfigureAwait(false); exitConfirmed = true; }
            catch (Exception waitError) when (waitError is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await Task.WhenAll(all).WaitAsync(CleanupTimeout).ConfigureAwait(false); }
            catch { /* All pipe faults are observed; the first cause remains authoritative. */ }
            if (!exitConfirmed) throw new ProcessFault("snapshot_termination_unconfirmed", "Worker shutdown could not be confirmed; inspect outputState. No unrelated process was killed.", false, error);
            throw;
        }
    }

    private static async Task WriteInputAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        try { await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
        finally { stream.Close(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, string code, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[65536];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, System.Math.Min(buffer.Length, maximum - (int)bytes.Length + 1)), token).ConfigureAwait(false);
            if (count == 0) return bytes.ToArray();
            if (bytes.Length + count > maximum) throw new ProcessFault(code, $"Worker data exceeded the {maximum}-byte bound.");
            bytes.Write(buffer, 0, count);
        }
    }

    private static JsonElement ParseReceipt(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Expected a single receipt object.");
            CheckUnique(json.RootElement);
            return json.RootElement.Clone();
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        { throw new ProcessFault("snapshot_protocol_error", "Worker stdout was not one bounded, unambiguous UTF-8 JSON receipt.", inner: error); }
    }

    private static void CheckUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate receipt property."); CheckUnique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckUnique(item);
    }

    private static bool Integer(JsonElement value, string key, int expected) => value.TryGetProperty(key, out var field)
        && field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int number) && number == expected;
    private static bool Text(JsonElement value, string key, string expected) => value.TryGetProperty(key, out var field)
        && field.ValueKind == JsonValueKind.String && field.GetString() == expected;
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool SameAbsolutePath(string actual, string expected)
    {
        try { return Path.IsPathFullyQualified(actual) && Path.GetFullPath(actual).Equals(expected,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static void RejectReparseOutput(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new ProcessFault("snapshot_output_mismatch", "Expected snapshot output is a directory or reparse point; it was not read.");
    }
    private static SnapshotOutputState ObserveOutput(string path, bool existedBefore)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return new(path, "reparse-point", existedBefore);
            if ((attributes & FileAttributes.Directory) != 0) return new(path, "directory", existedBefore);
            return new(path, "file", existedBefore, new FileInfo(path).Length);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return new(path, "missing", existedBefore); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(path, "unknown", existedBefore, Detail: error.Message); }
    }

    private sealed record ProcessResult(int ExitCode, byte[] StandardOutput, string StandardError);
    private sealed class ProcessFault(string code, string message, bool exitConfirmed = true, Exception? inner = null) : Exception(message, inner)
    { public string Code { get; } = code; public bool ExitConfirmed { get; } = exitConfirmed; }
}

internal interface ISnapshotProcessFactory { ISnapshotProcess Start(ProcessStartInfo start); }
internal interface ISnapshotProcess : IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken token);
    void KillTree();
}
internal sealed class SnapshotProcessFactory : ISnapshotProcessFactory
{
    public ISnapshotProcess Start(ProcessStartInfo start) => new SnapshotProcess(Process.Start(start)
        ?? throw new InvalidOperationException("Configured snapshot worker did not start."));
    private sealed class SnapshotProcess(Process process) : ISnapshotProcess
    {
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken token) => process.WaitForExitAsync(token);
        public void KillTree() { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) when (process.HasExited) { } }
        public void Dispose() => process.Dispose();
    }
}

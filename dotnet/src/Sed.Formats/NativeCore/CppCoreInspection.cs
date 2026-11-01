using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;

[assembly: InternalsVisibleTo("Sed.Core.Tests")]

namespace Sed.Formats.NativeCore;

public enum CppCoreOperation { InspectJkl, InspectNativeSed }
public enum CppCoreProjectKind { Jk, Mots, Ijim }
public enum CppCoreInspectionOutcome { Inspected, DocumentRejected, ProcessFailed }

public sealed record CppCoreInspectionOptions(string ExecutablePath, string ExpectedExecutableSha256,
    string ExpectedSourceManifestSha256, TimeSpan Timeout);

public sealed record CppCoreInspectionObservation(CppCoreInspectionOutcome Outcome,
    CppCoreInspectionOptions Options, CppCoreOperation Operation, CppCoreProjectKind ProjectKind,
    string RequestId, string InputSha256, int InputBytes, string? ExecutableSha256,
    bool ProcessStarted, bool ProcessExitConfirmed, int? ExitCode, JsonElement? Response,
    string StandardError, string? FailureCode, string? Message, int? ProcessId = null);

public static class CppCoreInspector
{
    public const int MaxInputBytes = 16 * 1024 * 1024;
    public const int MaxOutputBytes = 64 * 1024;
    public const int MaxDiagnosticBytes = 16 * 1024;
    public const string UpstreamCommit = "b82255998fb733c8e8c87104eb7c17f27b48376f";

    public static Task<CppCoreInspectionObservation> InspectAsync(CppCoreInspectionOptions options,
        CppCoreOperation operation, CppCoreProjectKind projectKind, ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken = default) => InspectAsync(options, operation, projectKind, input,
            cancellationToken, new CppCoreProcessFactory());

    internal static Task<CppCoreInspectionObservation> InspectAsync(CppCoreInspectionOptions options,
        CppCoreOperation operation, CppCoreProjectKind projectKind, ReadOnlyMemory<byte> input,
        CancellationToken cancellationToken, ICppCoreProcessFactory processFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processFactory);
        if (input.Length > MaxInputBytes) throw new ArgumentOutOfRangeException(nameof(input), "Native inspection input exceeds 16 MiB.");
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (!Enum.IsDefined(projectKind)) throw new ArgumentOutOfRangeException(nameof(projectKind));
        if (string.IsNullOrWhiteSpace(options.ExecutablePath) || !Path.IsPathFullyQualified(options.ExecutablePath))
            throw new ArgumentException("An explicit absolute native bridge executable path is required.", nameof(options));
        if (!ValidHash(options.ExpectedExecutableSha256) || !ValidHash(options.ExpectedSourceManifestSha256))
            throw new ArgumentException("Explicit executable and source-manifest SHA-256 identities are required.", nameof(options));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(options), "Native inspection deadline must be positive and at most 60 seconds.");
        cancellationToken.ThrowIfCancellationRequested();
        // ReadOnlyMemory may reference a caller-owned mutable array. Capture it
        // synchronously before returning the Task or awaiting any file/process I/O.
        byte[] captured = input.ToArray();
        var capturedOptions = options with { ExecutablePath = Path.GetFullPath(options.ExecutablePath),
            ExpectedExecutableSha256 = options.ExpectedExecutableSha256.ToLowerInvariant(),
            ExpectedSourceManifestSha256 = options.ExpectedSourceManifestSha256.ToLowerInvariant() };
        return InspectCapturedAsync(capturedOptions, operation, projectKind, captured, cancellationToken, processFactory);
    }

    private static async Task<CppCoreInspectionObservation> InspectCapturedAsync(CppCoreInspectionOptions options,
        CppCoreOperation operation, CppCoreProjectKind kind, byte[] input, CancellationToken callerToken,
        ICppCoreProcessFactory factory)
    {
        string inputHash = Hash(input), requestId = Guid.NewGuid().ToString("N");
        string operationName = operation == CppCoreOperation.InspectJkl ? "inspect-jkl" : "inspect-native-sed";
        string kindName = kind switch { CppCoreProjectKind.Jk => "jk", CppCoreProjectKind.Mots => "mots", _ => "ijim" };
        string? binaryHash = null;
        bool started = false, exitConfirmed = true;
        int? exit = null, processId = null;
        string stderr = "";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        deadline.CancelAfter(options.Timeout);
        try
        {
            binaryHash = await VerifyExecutableAsync(options, deadline.Token).ConfigureAwait(false);
            var capabilities = await Run(["--protocol", "1", "capabilities"], []).ConfigureAwait(false);
            CppCoreProtocol.Validate(capabilities.Output, capabilities.ExitCode, "capabilities", null, null, 0, options.ExpectedSourceManifestSha256);
            await VerifyExecutableAsync(options, deadline.Token).ConfigureAwait(false);
            var inspection = await Run(["--protocol", "1", operationName, "--project-kind", kindName, "--request-id", requestId], input).ConfigureAwait(false);
            await VerifyExecutableAsync(options, deadline.Token).ConfigureAwait(false);
            var response = CppCoreProtocol.Validate(inspection.Output, inspection.ExitCode, operationName, requestId,
                kindName, input.Length, options.ExpectedSourceManifestSha256);
            string status = response.GetProperty("status").GetString()!;
            var outcome = status == "ok" ? CppCoreInspectionOutcome.Inspected
                : status == "rejected" ? CppCoreInspectionOutcome.DocumentRejected : CppCoreInspectionOutcome.ProcessFailed;
            return Observation(outcome, response, status == "ok" ? null : response.GetProperty("error").GetProperty("code").GetString(),
                status == "ok" ? null : response.GetProperty("error").GetProperty("message").GetString());
        }
        catch (CppCoreFault error)
        {
            exitConfirmed = error.ExitConfirmed;
            return Observation(CppCoreInspectionOutcome.ProcessFailed, null, error.Code, error.Message);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            // ExecuteAsync only propagates cancellation after owned-child exit
            // is confirmed. Unconfirmed cleanup becomes a CppCoreFault above.
            throw new OperationCanceledException("Native inspection cancelled after owned-child cleanup.", callerToken);
        }
        catch (OperationCanceledException)
        { exitConfirmed = true; return Observation(CppCoreInspectionOutcome.ProcessFailed, null, "timeout", "Native inspection exceeded its deadline."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return Observation(CppCoreInspectionOutcome.ProcessFailed, null, "process_error", error.Message); }

        async Task<CppCoreProcessResult> Run(string[] arguments, byte[] bytes)
        {
            deadline.Token.ThrowIfCancellationRequested();
            exit = null;
            stderr = "";
            var result = await CppCoreProcessRunner.ExecuteAsync(options.ExecutablePath, arguments, bytes, factory,
                pid => { started = true; exitConfirmed = false; processId = pid; }, deadline.Token).ConfigureAwait(false);
            exitConfirmed = true;
            exit = result.ExitCode;
            stderr = result.StandardError;
            return result;
        }

        CppCoreInspectionObservation Observation(CppCoreInspectionOutcome outcome, JsonElement? response, string? code, string? message)
            => new(outcome, options, operation, kind, requestId, inputHash, input.Length, binaryHash, started,
                exitConfirmed, exit, response, stderr, code, message, processId);
    }

    private static async Task<string> VerifyExecutableAsync(CppCoreInspectionOptions options, CancellationToken token)
    {
        string path = options.ExecutablePath;
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new CppCoreFault("executable_identity_mismatch", "The configured native executable path contains a reparse point.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.Directory) != 0 || info.Length is <= 0 or > 512L * 1024 * 1024)
            throw new CppCoreFault("executable_identity_mismatch", "The configured executable is not a bounded nonempty regular file.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        long capturedLength = stream.Length;
        if (capturedLength is <= 0 or > 512L * 1024 * 1024)
            throw new CppCoreFault("executable_identity_mismatch", "Native executable size changed outside its hashing budget.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        for (long remaining = capturedLength; remaining > 0;)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token).ConfigureAwait(false);
            if (count == 0) throw new CppCoreFault("executable_identity_mismatch", "Native executable shrank during hashing.");
            digest.AppendData(buffer, 0, count);
            remaining -= count;
        }
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0 || stream.Length != capturedLength)
            throw new CppCoreFault("executable_identity_mismatch", "Native executable changed size during hashing.");
        string hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
        if (hash != options.ExpectedExecutableSha256)
            throw new CppCoreFault("executable_identity_mismatch", "Native executable SHA-256 does not match the explicitly configured identity.");
        return hash;
    }

    private static bool ValidHash(string? text) => text is { Length: 64 } && text.All(Uri.IsHexDigit);
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}

internal interface ICppCoreProcessFactory
{
    ICppCoreProcess Start(ProcessStartInfo start);
}

internal interface ICppCoreProcess : IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    int ExitCode { get; }
    bool HasExited { get; }
    int? Id { get; }
    Task WaitForExitAsync(CancellationToken token);
    void KillTree();
}

internal sealed class CppCoreFault(string code, string message, bool exitConfirmed = true, Exception? inner = null)
    : Exception(message, inner)
{
    internal string Code { get; } = code;
    internal bool ExitConfirmed { get; } = exitConfirmed;
}

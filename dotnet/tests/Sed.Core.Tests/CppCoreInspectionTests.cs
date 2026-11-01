using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Sed.Formats.NativeCore;

namespace Sed.Core.Tests;

public sealed class CppCoreInspectionTests
{
    private const string ManifestHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string Upstream = "b82255998fb733c8e8c87104eb7c17f27b48376f";

    [Fact]
    public async Task SuccessBindsCapturedBytesBinaryOptionsAndBothWorkerIdentities()
    {
        using var fixture = new Fixture();
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.Inspected, result.Outcome);
        Assert.Equal(Hash(fixture.Input), result.InputSha256);
        Assert.Equal(fixture.Options.ExpectedExecutableSha256, result.ExecutableSha256);
        Assert.Equal(fixture.Input.Length, result.InputBytes);
        Assert.True(result.ProcessExitConfirmed);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ManifestHash, result.Response!.Value.GetProperty("sourceManifestSha256").GetString());
        Assert.False(result.Response.Value.GetProperty("resourceValidation").GetProperty("complete").GetBoolean());
        Assert.Equal(2, fixture.Factory.Starts.Count);
        Assert.Equal(new[] { "--protocol", "1", "capabilities" }, fixture.Factory.Starts[0].ArgumentList);
        var inspection = fixture.Factory.Starts[1];
        Assert.Equal(new[] { "--protocol", "1", "inspect-jkl", "--project-kind", "jk", "--request-id", result.RequestId }, inspection.ArgumentList);
        Assert.Matches("^[0-9a-f]{32}$", result.RequestId);
        foreach (var start in fixture.Factory.Starts)
        {
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Empty(start.Arguments);
            Assert.Equal(fixture.Executable, start.FileName);
        }
        Assert.Equal(fixture.Input, fixture.Factory.Children[1].Input.ToArray());
    }

    [Fact]
    public async Task MutableMemoryBackingIsSnapshottedBeforeTheFirstAwaitAndHandshake()
    {
        using var fixture = new Fixture();
        byte[] original = fixture.Input.ToArray();
        fixture.Factory.OnStart = start => { if (start.ArgumentList[2] == "capabilities") fixture.Input[0] ^= 0xff; };
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.Inspected, result.Outcome);
        Assert.Equal(Hash(original), result.InputSha256);
        Assert.Equal(original, fixture.Factory.Children[1].Input.ToArray());
    }

    [Fact]
    public async Task ChangedExecutableIsRefusedBeforeAnyWorkerStarts()
    {
        using var fixture = new Fixture();
        File.AppendAllText(fixture.Executable, "changed");
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, result.Outcome);
        Assert.Equal("executable_identity_mismatch", result.FailureCode);
        Assert.Empty(fixture.Factory.Starts);
    }

    [Fact]
    public async Task IdentityDriftDuringCapabilitiesPreventsDocumentSubmission()
    {
        using var fixture = new Fixture();
        fixture.Factory.OnStart = _ => File.AppendAllText(fixture.Executable, "changed");
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, result.Outcome);
        Assert.Equal("executable_identity_mismatch", result.FailureCode);
        Assert.Single(fixture.Factory.Starts);
    }

    [Theory]
    [InlineData("requestId", "00000000000000000000000000000000")]
    [InlineData("operation", "inspect-native-sed")]
    [InlineData("name", "unrelated-worker")]
    [InlineData("version", "0.2.0")]
    [InlineData("upstreamCommit", "unreviewed")]
    [InlineData("sourceManifestSha256", "1111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("projectKind", "mots")]
    public async Task MismatchedObservationCannotBecomeAnInspection(string field, string value)
    {
        using var fixture = new Fixture();
        fixture.Factory.ChangeInspection = json => json[field] = value;
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, result.Outcome);
        Assert.Equal("protocol_error", result.FailureCode);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("status")]
    [InlineData("readOnly")]
    [InlineData("inputBytes")]
    [InlineData("requestId")]
    public async Task RequiredEnvelopeFieldsCannotBeOmitted(string field)
    {
        using var fixture = new Fixture();
        fixture.Factory.ChangeInspection = json => json.Remove(field);
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, (await fixture.Inspect()).Outcome);
    }

    [Theory]
    [InlineData(2, "invalid_request")]
    [InlineData(2, "input_limit")]
    [InlineData(3, "parse_error")]
    [InlineData(3, "project_kind_mismatch")]
    public async Task ValidDocumentRejectionIsSeparateFromAProcessFault(int exit, string code)
    {
        using var fixture = new Fixture();
        fixture.Factory.InspectionExitCode = exit;
        fixture.Factory.ChangeInspection = json => Reject(json, "rejected", code);
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.DocumentRejected, result.Outcome);
        Assert.Equal(exit, result.ExitCode);
        Assert.Equal(code, result.FailureCode);
        Assert.True(result.ProcessExitConfirmed);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}{}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"nested\":{\"a\":1,\"a\":2}}")]
    public async Task MalformedAndAmbiguousJsonAreProcessFaults(string text)
    {
        using var fixture = new Fixture();
        fixture.Factory.InspectionOutput = Encoding.UTF8.GetBytes(text + "\n");
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, result.Outcome);
        Assert.Equal("protocol_error", result.FailureCode);
    }

    [Fact]
    public async Task OversizedInputIsRejectedBeforeCaptureAllocationOrLaunch()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CppCoreInspector.InspectAsync(fixture.Options,
            CppCoreOperation.InspectJkl, CppCoreProjectKind.Jk, new byte[16 * 1024 * 1024 + 1], default, fixture.Factory));
        Assert.Empty(fixture.Factory.Starts);
    }

    [Fact]
    public async Task BadCapabilitiesIdentityNeverSubmitsDocumentBytes()
    {
        using var fixture = new Fixture();
        fixture.Factory.ChangeCapabilities = json => json["sourceManifestSha256"] = new string('0', 64);
        var result = await fixture.Inspect();
        Assert.Equal("protocol_error", result.FailureCode);
        Assert.Single(fixture.Factory.Starts);
        Assert.Empty(fixture.Factory.Children[0].Input.ToArray());
    }

    [Fact]
    public async Task IdentityDriftDuringInspectionInvalidatesAnOtherwiseValidObservation()
    {
        using var fixture = new Fixture();
        fixture.Factory.OnStart = start => { if (start.ArgumentList[2] != "capabilities") File.AppendAllText(fixture.Executable, "changed"); };
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, result.Outcome);
        Assert.Equal("executable_identity_mismatch", result.FailureCode);
        Assert.Null(result.Response);
        Assert.Equal(2, fixture.Factory.Starts.Count);
    }

    [Theory]
    [InlineData(0, "rejected", "parse_error")]
    [InlineData(2, "rejected", "parse_error")]
    [InlineData(3, "failed", "parse_error")]
    [InlineData(5, "rejected", "io_error")]
    [InlineData(17, "failed", "internal_error")]
    public async Task StatusAndErrorCodeMustMatchProcessExit(int exit, string status, string code)
    {
        using var fixture = new Fixture();
        fixture.Factory.InspectionExitCode = exit;
        fixture.Factory.ChangeInspection = json => Reject(json, status, code);
        Assert.Equal("protocol_error", (await fixture.Inspect()).FailureCode);
    }

    [Theory]
    [InlineData(5, "io_error")]
    [InlineData(6, "internal_error")]
    [InlineData(6, "output_limit")]
    public async Task ValidWorkerFailureRemainsDistinctFromDocumentRejection(int exit, string code)
    {
        using var fixture = new Fixture();
        fixture.Factory.InspectionExitCode = exit;
        fixture.Factory.ChangeInspection = json => Reject(json, "failed", code);
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, result.Outcome);
        Assert.Equal(code, result.FailureCode);
        Assert.NotNull(result.Response);
        Assert.Equal(exit, result.ExitCode);
    }

    [Fact]
    public async Task ReadOnlyAndIncompleteResourceClaimsAreEnforced()
    {
        foreach (var field in new[] { "readOnly", "resourceValidation" })
        {
            using var fixture = new Fixture();
            fixture.Factory.ChangeInspection = json =>
            {
                if (field == "readOnly") json[field] = false;
                else json[field]!["complete"] = true;
            };
            Assert.Equal("protocol_error", (await fixture.Inspect()).FailureCode);
        }
    }

    [Fact]
    public async Task ByteCountAndUnknownSchemaCannotPass()
    {
        foreach (var field in new[] { "inputBytes", "schemaVersion" })
        {
            using var fixture = new Fixture();
            fixture.Factory.ChangeInspection = json => json[field] = 999;
            Assert.Equal("protocol_error", (await fixture.Inspect()).FailureCode);
        }
    }

    [Fact]
    public async Task InvalidUtf8CannotBecomeAReplacementDecodedReceipt()
    {
        using var fixture = new Fixture();
        fixture.Factory.ChangeInspection = json => json["diagnostics"] = new JsonArray(
            new JsonObject { ["severity"] = "info", ["message"] = "source-marker" });
        fixture.Factory.CorruptUtf8 = true;
        Assert.Equal("protocol_error", (await fixture.Inspect()).FailureCode);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("nested")]
    [InlineData("trailing")]
    public async Task OtherwiseValidReceiptRejectsDuplicateKeysAndTrailingValue(string mode)
    {
        using var fixture = new Fixture();
        fixture.Factory.TransformInspectionText = text => mode switch
        {
            "root" => text.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            "nested" => text.Replace("\"sectors\":0", "\"sectors\":0,\"sectors\":0", StringComparison.Ordinal),
            _ => text + "{}"
        };
        Assert.Equal("protocol_error", (await fixture.Inspect()).FailureCode);
    }

    [Fact]
    public async Task CompleteReceiptRequiresTheProtocolLineTerminator()
    {
        using var fixture = new Fixture();
        fixture.Factory.OmitFinalLf = true;
        Assert.Equal("protocol_error", (await fixture.Inspect()).FailureCode);
    }

    [Theory]
    [InlineData("clean", true, 0)]
    [InlineData("clean", false, 1)]
    [InlineData("repaired", false, 0)]
    public async Task ContradictoryNativeIntegrityCannotCountAsSuccessfulInspection(string status, bool corruption, int repairs)
    {
        using var fixture = new Fixture();
        fixture.Factory.ChangeInspection = json =>
        {
            json["integrity"]!["status"] = status;
            json["integrity"]!["hadCorruption"] = corruption;
            json["integrity"]!["repairedChunks"] = repairs;
        };
        var result = await CppCoreInspector.InspectAsync(fixture.Options, CppCoreOperation.InspectNativeSed,
            CppCoreProjectKind.Mots, fixture.Input, default, fixture.Factory, fixture.Clock);
        Assert.Equal("protocol_error", result.FailureCode);
    }

    [Fact]
    public async Task OutputOverflowIsAProcessFault()
    {
        using var fixture = new Fixture();
        fixture.Factory.InspectionOutput = new byte[65537];
        var result = await fixture.Inspect();
        Assert.Equal("stdout_limit", result.FailureCode);
        Assert.True(result.ProcessExitConfirmed);
    }

    [Fact]
    public async Task AlreadyCancelledInspectionStartsNothing()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CppCoreInspector.InspectAsync(fixture.Options,
            CppCoreOperation.InspectJkl, CppCoreProjectKind.Jk, fixture.Input, new CancellationToken(true), fixture.Factory));
        Assert.Empty(fixture.Factory.Starts);
    }

    [Fact]
    public async Task ErrorPipeOverflowCannotHangOrCountAsDocumentRejection()
    {
        using var fixture = new Fixture();
        fixture.Factory.InspectionErrorOutput = new byte[16385];
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, result.Outcome);
        Assert.Equal("stderr_limit", result.FailureCode);
        Assert.True(result.ProcessExitConfirmed);
    }

    [Fact]
    public async Task TimeoutStopsTheOwnedProcessBeforeReturning()
    {
        using var fixture = new Fixture();
        var child = new BlockingProcess();
        fixture.Factory.InspectionProcess = () => child;
        var timeout = TimeSpan.FromMilliseconds(100);
        bool advancedAfterInspectionStart = false;
        child.OnWaitStarted = () =>
        {
            // The runner calls this only after obtaining the inspection child,
            // recording ownership and starting its stdin/stdout/stderr work.
            advancedAfterInspectionStart = fixture.Factory.Starts.Count == 2
                && fixture.Factory.Starts[1].ArgumentList[2] == "inspect-jkl";
            fixture.Clock.Advance(timeout);
        };
        var result = await CppCoreInspector.InspectAsync(fixture.Options with { Timeout = timeout },
            CppCoreOperation.InspectJkl, CppCoreProjectKind.Jk, fixture.Input, default, fixture.Factory, fixture.Clock);
        Assert.True(advancedAfterInspectionStart);
        Assert.True(result.ProcessStarted);
        Assert.Equal("timeout", result.FailureCode);
        Assert.True(child.Killed && child.HasExited);
        Assert.True(result.ProcessExitConfirmed);
    }

    [Fact]
    public async Task UnconfirmedTerminationIsNotReportedAsACleanTimeout()
    {
        using var fixture = new Fixture();
        var child = new BlockingProcess { ConfirmTermination = false };
        fixture.Factory.InspectionProcess = () => child;
        var timeout = TimeSpan.FromMilliseconds(100);
        bool advancedAfterInspectionStart = false;
        child.OnWaitStarted = () =>
        {
            advancedAfterInspectionStart = fixture.Factory.Starts.Count == 2
                && fixture.Factory.Starts[1].ArgumentList[2] == "inspect-jkl";
            fixture.Clock.Advance(timeout);
        };
        var result = await CppCoreInspector.InspectAsync(fixture.Options with { Timeout = timeout },
            CppCoreOperation.InspectJkl, CppCoreProjectKind.Jk, fixture.Input, default, fixture.Factory, fixture.Clock);
        Assert.True(advancedAfterInspectionStart);
        Assert.True(result.ProcessStarted);
        Assert.Equal("termination_unconfirmed", result.FailureCode);
        Assert.False(result.ProcessExitConfirmed);
        Assert.True(child.Killed);
    }

    [Fact]
    public async Task DeadlineExpiredBeforeIdentityReadStartsNoWorker()
    {
        using var fixture = new Fixture();
        fixture.Clock.ExpireNextTimerOnCreation = true;
        var result = await fixture.Inspect();
        Assert.Equal(CppCoreInspectionOutcome.ProcessFailed, result.Outcome);
        Assert.Equal("timeout", result.FailureCode);
        Assert.Empty(fixture.Factory.Starts);
        Assert.False(result.ProcessStarted);
        Assert.True(result.ProcessExitConfirmed);
        Assert.Null(result.ExitCode);
        Assert.Null(result.ProcessId);
    }

    [CppCoreLinuxPythonFact]
    public async Task ActualBlockedStdinTimesOutAndOwnedChildIsReaped()
    {
        using var child = new PythonChild("block-stdin");
        var result = await CppCoreInspector.InspectAsync(child.Options, CppCoreOperation.InspectJkl,
            CppCoreProjectKind.Jk, new byte[1024 * 1024]);
        Assert.Equal("timeout", result.FailureCode);
        Assert.True(result.ProcessExitConfirmed);
        child.AssertReaped();
    }

    [CppCoreLinuxPythonFact]
    public async Task ActualStdoutOverflowStopsAndReapsTheOwnedChild()
    {
        using var child = new PythonChild("stdout-overflow");
        var result = await CppCoreInspector.InspectAsync(child.Options, CppCoreOperation.InspectJkl,
            CppCoreProjectKind.Jk, Encoding.ASCII.GetBytes("original synthetic input"));
        Assert.Equal("stdout_limit", result.FailureCode);
        Assert.True(result.ProcessExitConfirmed);
        child.AssertReaped();
    }

    [CppCoreLinuxPythonFact]
    public async Task ActualCancellationWaitsForChildReaping()
    {
        using var child = new PythonChild("block-stdin");
        using var cancel = new CancellationTokenSource();
        Task<CppCoreInspectionObservation> task = CppCoreInspector.InspectAsync(child.Options with { Timeout = TimeSpan.FromSeconds(5) },
            CppCoreOperation.InspectJkl, CppCoreProjectKind.Jk, new byte[1024 * 1024], cancel.Token);
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(child.PidFile) && deadline.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
        Assert.True(File.Exists(child.PidFile), "Synthetic inspection child must have started before cancellation.");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        child.AssertReaped();
    }

    private static void Reject(JsonObject json, string status, string code)
    {
        foreach (string field in new[] { "summary", "integrity", "optionalSections", "optionalSectionsTruncated", "diagnostics", "diagnosticsTruncated", "projectKind", "uvCoordinateSpace", "resourceValidation" })
            json.Remove(field);
        json["status"] = status;
        json["error"] = new JsonObject { ["code"] = code, ["message"] = "Original synthetic rejection." };
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "sed-cpp-client-" + Guid.NewGuid().ToString("N"));
        internal readonly string Executable;
        internal readonly byte[] Input = Encoding.Latin1.GetBytes("SECTION: HEADER\nVersion 1\n# Original synthetic fixture\n");
        internal readonly CppCoreInspectionOptions Options;
        internal readonly FakeFactory Factory;
        internal readonly ManualDeadlineTimeProvider Clock = new();
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Executable = Path.Combine(Root, OperatingSystem.IsWindows() ? "sed-cpp-bridge.exe" : "sed-cpp-bridge");
            File.WriteAllText(Executable, "Original fake binary identity; this file is never executed.");
            Options = new(Executable, Hash(File.ReadAllBytes(Executable)), ManifestHash, TimeSpan.FromSeconds(2));
            Factory = new(Input.Length);
        }
        internal Task<CppCoreInspectionObservation> Inspect() => CppCoreInspector.InspectAsync(Options,
            CppCoreOperation.InspectJkl, CppCoreProjectKind.Jk, Input, default, Factory, Clock);
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeFactory(int inputBytes) : ICppCoreProcessFactory
    {
        internal readonly List<ProcessStartInfo> Starts = [];
        internal readonly List<FakeProcess> Children = [];
        internal Action<ProcessStartInfo>? OnStart;
        internal Action<JsonObject>? ChangeInspection;
        internal Action<JsonObject>? ChangeCapabilities;
        internal byte[]? InspectionOutput;
        internal byte[]? InspectionErrorOutput;
        internal Func<ICppCoreProcess>? InspectionProcess;
        internal int InspectionExitCode;
        internal bool OmitFinalLf;
        internal bool CorruptUtf8;
        internal Func<string, string>? TransformInspectionText;
        public ICppCoreProcess Start(ProcessStartInfo start)
        {
            Starts.Add(start);
            OnStart?.Invoke(start);
            bool capabilities = start.ArgumentList[2] == "capabilities";
            if (!capabilities && InspectionProcess is not null) return InspectionProcess();
            var json = new JsonObject
            {
                ["schemaVersion"] = 1, ["name"] = "sed-cpp-bridge", ["version"] = "0.1.0",
                ["upstreamCommit"] = Upstream, ["sourceManifestSha256"] = ManifestHash,
                ["operation"] = start.ArgumentList[2], ["requestId"] = capabilities ? null : start.ArgumentList[6],
                ["status"] = "ok", ["readOnly"] = true, ["inputBytes"] = capabilities ? 0 : inputBytes
            };
            if (capabilities)
            {
                json["capabilities"] = new JsonArray("inspect-native-sed", "inspect-jkl");
                json["projectKinds"] = new JsonArray("jk", "mots", "ijim");
                json["writesFiles"] = false;
                json["limits"] = new JsonObject { ["maxInputBytes"] = 16777216, ["maxOutputBytes"] = 65536,
                    ["maxDiagnostics"] = 32, ["maxDiagnosticBytes"] = 256, ["maxOptionalSections"] = 32, ["maxIntegrityItems"] = 32,
                    ["maxJklLines"] = 131072, ["maxJklLineBytes"] = 65536, ["maxJklTokens"] = 1048576,
                    ["maxJklSurfaceReferences"] = 131072, ["maxJklSurfaceExpansionTokens"] = 1048576, ["maxJklExpansionBytes"] = 67108864 };
                ChangeCapabilities?.Invoke(json);
            }
            else
            {
                json["projectKind"] = start.ArgumentList[4]; json["uvCoordinateSpace"] = "texture-pixels";
                json["summary"] = new JsonObject { ["sectors"] = 0, ["surfaces"] = 0, ["vertices"] = 0, ["things"] = 0,
                    ["templates"] = 0, ["cogs"] = 0, ["lights"] = 0, ["materials"] = 0, ["colormaps"] = 0, ["architecturalLightingObjects"] = 0 };
                json["resourceValidation"] = new JsonObject { ["status"] = "not-performed", ["complete"] = false,
                    ["cogHydration"] = "deferred", ["uvNormalization"] = "not-performed" };
                json["diagnostics"] = new JsonArray(); json["diagnosticsTruncated"] = false;
                json["integrity"] = new JsonObject { ["status"] = "not-applicable", ["hadCorruption"] = false,
                    ["repairedChunks"] = 0, ["repairedSections"] = new JsonArray(), ["unreadableRanges"] = new JsonArray(), ["truncated"] = false };
                json["optionalSections"] = new JsonArray(); json["optionalSectionsTruncated"] = false;
                ChangeInspection?.Invoke(json);
            }
            string text = json.ToJsonString();
            if (!capabilities && TransformInspectionText is not null) text = TransformInspectionText(text);
            byte[] bytes = Encoding.UTF8.GetBytes(text + (!capabilities && OmitFinalLf ? "" : "\n"));
            if (!capabilities && CorruptUtf8) bytes[bytes.AsSpan().IndexOf("source-marker"u8)] = 0xff;
            var process = new FakeProcess(!capabilities && InspectionOutput is not null ? InspectionOutput : bytes,
                capabilities ? 0 : InspectionExitCode, capabilities ? null : InspectionErrorOutput);
            Children.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess(byte[] output, int exitCode, byte[]? errorOutput = null) : ICppCoreProcess
    {
        internal readonly MemoryStream Input = new();
        private readonly MemoryStream _output = new(output);
        private readonly MemoryStream _error = new(errorOutput ?? []);
        public Stream StandardInput => Input;
        public Stream StandardOutput => _output;
        public Stream StandardError => _error;
        public int ExitCode => exitCode;
        public bool HasExited => true;
        public int? Id => null;
        public Task WaitForExitAsync(CancellationToken token) => Task.CompletedTask;
        public void KillTree() { }
        public void Dispose() { Input.Dispose(); _output.Dispose(); _error.Dispose(); }
    }

    private sealed class BlockingProcess : ICppCoreProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput { get; } = new PendingReadStream();
        public Stream StandardError { get; } = new PendingReadStream();
        public int ExitCode => -1;
        public bool HasExited => _exit.Task.IsCompletedSuccessfully;
        public int? Id => null;
        internal bool ConfirmTermination = true;
        internal bool Killed;
        internal Action? OnWaitStarted;
        private bool _waitStarted;
        public Task WaitForExitAsync(CancellationToken token)
        {
            if (token.CanBeCanceled && !_waitStarted)
            {
                _waitStarted = true;
                OnWaitStarted?.Invoke();
            }
            return !token.CanBeCanceled && !ConfirmTermination
                ? Task.FromException(new InvalidOperationException("Synthetic unconfirmed termination.")) : _exit.Task.WaitAsync(token);
        }
        public void KillTree() { Killed = true; if (ConfirmTermination) _exit.TrySetResult(); }
        public void Dispose() { StandardInput.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); }
    }

    // Deliberately limited to the one-shot timers used by CancellationTokenSource.
    // Each fixture owns its clock; no global clock or real-time sleep is changed.
    private sealed class ManualDeadlineTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        internal bool ExpireNextTimerOnCreation;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new ManualTimer(this, callback, state);
            bool expire;
            lock (_gate)
            {
                _timers.Add(timer);
                timer.Change(dueTime, period);
                expire = ExpireNextTimerOnCreation;
                ExpireNextTimerOnCreation = false;
            }
            // Timer callbacks can race construction in production too. Advancing
            // here deterministically exercises expiry before any identity I/O.
            if (expire) Advance(dueTime);
            return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _ticks = checked(_ticks + elapsed.Ticks);
                foreach (var timer in _timers)
                    if (timer.DueTicks is long due && due <= _ticks)
                    {
                        timer.DueTicks = null;
                        callbacks.Add((timer.Callback, timer.State));
                    }
            }
            // Cancellation disposes its timer; invoke outside the collection lock.
            foreach (var (callback, state) in callbacks) callback(state);
        }

        private sealed class ManualTimer(ManualDeadlineTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long? DueTicks;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException("Only one-shot fixture timers are supported.");
                if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
                    throw new ArgumentOutOfRangeException(nameof(dueTime));
                lock (owner._gate)
                {
                    if (_disposed) return false;
                    DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : checked(owner._ticks + dueTime.Ticks);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._gate)
                {
                    _disposed = true;
                    DueTicks = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class PendingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PythonChild : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "sed-cpp-child-" + Guid.NewGuid().ToString("N"));
        internal readonly string PidFile;
        internal readonly CppCoreInspectionOptions Options;
        internal PythonChild(string mode)
        {
            Directory.CreateDirectory(_root);
            string executable = Path.Combine(_root, "sed-cpp-bridge");
            PidFile = executable + ".pid";
            string script = "#!/usr/bin/python3\n" + $$"""
                import json, os, pathlib, sys, time
                args = sys.argv[1:]
                assert args[:2] == ['--protocol', '1']
                if args[2] == 'capabilities':
                    assert len(args) == 3
                    reply = dict(schemaVersion=1, name='sed-cpp-bridge', version='0.1.0',
                        upstreamCommit='b82255998fb733c8e8c87104eb7c17f27b48376f',
                        sourceManifestSha256='abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789',
                        operation='capabilities', requestId=None, status='ok', readOnly=True, inputBytes=0,
                        capabilities=['inspect-native-sed','inspect-jkl'], projectKinds=['jk','mots','ijim'], writesFiles=False,
                        limits=dict(maxInputBytes=16777216,maxOutputBytes=65536,maxDiagnostics=32,maxDiagnosticBytes=256,
                            maxOptionalSections=32,maxIntegrityItems=32,maxJklLines=131072,maxJklLineBytes=65536,
                            maxJklTokens=1048576,maxJklSurfaceReferences=131072,maxJklSurfaceExpansionTokens=1048576,maxJklExpansionBytes=67108864))
                    print(json.dumps(reply))
                    sys.exit(0)
                assert len(args) == 7 and args[2:6] == ['inspect-jkl','--project-kind','jk','--request-id']
                pathlib.Path(__file__ + '.pid').write_text(str(os.getpid()))
                if '{{mode}}' == 'stdout-overflow':
                    sys.stdout.buffer.write(b'x' * 65537)
                    sys.stdout.buffer.flush()
                time.sleep(60)
                """;
            File.WriteAllText(executable, script.Replace("\r\n", "\n"), new UTF8Encoding(false));
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Options = new(executable, Hash(File.ReadAllBytes(executable)), ManifestHash, TimeSpan.FromSeconds(1));
        }
        internal void AssertReaped()
        {
            Assert.True(File.Exists(PidFile), "Synthetic inspection worker did not start.");
            int pid = int.Parse(File.ReadAllText(PidFile));
            Assert.False(Directory.Exists("/proc/" + pid), "Owned synthetic child must be reaped before returning.");
        }
        public void Dispose()
        {
            if (File.Exists(PidFile) && int.TryParse(File.ReadAllText(PidFile), out int pid))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    // Test safety fallback: only the exact stand-in script we
                    // wrote can be stopped if the baseline implementation leaks.
                    string command = File.ReadAllText("/proc/" + pid + "/cmdline");
                    if (command.Contains(Path.Combine(_root, "sed-cpp-bridge"), StringComparison.Ordinal))
                    { process.Kill(entireProcessTree: true); process.WaitForExit(5000); }
                }
                catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException) { }
            }
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class CppCoreLinuxPythonFactAttribute : FactAttribute
{
    public CppCoreLinuxPythonFactAttribute()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3")) Skip = "Actual Linux synthetic process test requires /usr/bin/python3.";
    }
}

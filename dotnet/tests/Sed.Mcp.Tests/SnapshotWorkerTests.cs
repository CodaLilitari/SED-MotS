using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sed.Mcp.Infrastructure;

namespace Sed.Mcp.Tests;

/// <summary>Original process fixtures exercise the adapter contract without a GPU, game data or a shell.</summary>
public sealed class SnapshotWorkerTests
{
    private const string Version = "0.4.0-mots.test+0123456789";
    // One-pixel synthetic PNG fixture; no game or third-party level data.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");

    [Fact]
    public async Task SuccessUsesOnlyFixedArgumentsChecksIdentityAndReturnsExactBytes()
    {
        using var fixture = new Fixture();
        FakeProcess? snapshot = null;
        var factory = new FakeFactory(fixture.VersionProcess, () => snapshot = fixture.Success());
        var worker = fixture.Worker(factory);
        const string request = "{\"label\":\"é\",\"value\":\"a; & b\"}";

        var result = await worker.RunAsync(fixture.Level, request, fixture.Output, CancellationToken.None);

        Assert.Equal(Png, result.ImageBytes);
        Assert.Equal(Hash(Png), result.Sha256);
        Assert.Equal("file", result.OutputState.Status);
        Assert.False(result.OutputState.ExistedBefore);
        Assert.Equal(Png.Length, result.OutputState.Bytes);
        Assert.True(result.OutputState.ProcessExitConfirmed);
        Assert.Equal("renderer diagnostic", result.StandardError);
        Assert.Equal(2, factory.Starts.Count);
        Assert.Equal(new[] { "--version" }, factory.Starts[0].ArgumentList);
        Assert.Equal(new[] { "snapshot", fixture.Level, "-", fixture.Output }, factory.Starts[1].ArgumentList);
        foreach (var start in factory.Starts)
        {
            Assert.Equal(fixture.Agent, start.FileName);
            Assert.Equal(fixture.Root, start.WorkingDirectory);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
            Assert.Empty(start.Arguments);
        }
        Assert.NotNull(snapshot);
        Assert.Equal(Encoding.UTF8.GetBytes(request), snapshot.Input.ToArray());
        Assert.True(snapshot.Disposed);
        Assert.False(snapshot.Killed);
    }

    [Fact]
    public async Task ReusesVersionHandshakeButRehashesManagedFilesOnEveryRun()
    {
        using var fixture = new Fixture();
        string secondOutput = Path.Combine(fixture.Root, "second.png");
        var factory = new FakeFactory(fixture.VersionProcess,
            () => fixture.Success(), () => fixture.Success(secondOutput));
        var worker = fixture.Worker(factory);
        await worker.RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None);
        await worker.RunAsync(fixture.Level, "{}", secondOutput, CancellationToken.None);
        Assert.Equal(3, factory.Starts.Count);
        Assert.Equal(1, factory.Starts.Count(start => start.ArgumentList[0] == "--version"));
    }

    [Theory]
    [InlineData("sed-agent.exe")]
    [InlineData("sed-agent.dll")]
    [InlineData("Sed.Core.dll")]
    [InlineData("Sed.Formats.dll")]
    [InlineData("Sed.Rendering.dll")]
    [InlineData("Sed.Rendering.Vulkan.dll")]
    public async Task RefusesChangedExecutableOrManagedCompanionBeforeNextLaunch(string component)
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(fixture.VersionProcess, () => fixture.Success());
        var worker = fixture.Worker(factory);
        await worker.RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None);
        File.AppendAllText(Path.Combine(fixture.Root, component), "changed");
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => worker.RunAsync(
            fixture.Level, "{}", Path.Combine(fixture.Root, "second.png"), CancellationToken.None));
        Assert.Equal("snapshot_identity_changed", error.Code);
        Assert.Equal(2, factory.Starts.Count);
    }

    [Fact]
    public async Task DetectsIdentityChangeDuringVersionHandshakeBeforeSnapshot()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(() =>
        {
            File.AppendAllText(Path.Combine(fixture.Root, "sed-agent.dll"), "changed");
            return fixture.VersionProcess();
        });
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_identity_changed", error.Code);
        Assert.Single(factory.Starts);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public async Task DetectsIdentityChangeAfterSnapshotAndPreservesObservedOutput()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(fixture.VersionProcess, () =>
        {
            var process = fixture.Success();
            File.AppendAllText(Path.Combine(fixture.Root, "Sed.Rendering.dll"), "changed");
            return process;
        });
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_identity_changed", error.Code);
        Assert.Equal("file", error.OutputState.Status);
        Assert.Equal(Png, File.ReadAllBytes(fixture.Output));
    }

    [Theory]
    [InlineData("different-version", "sed-agent", 1, 0)]
    [InlineData(Version, "different-agent", 1, 0)]
    [InlineData(Version, "sed-agent", 2, 0)]
    [InlineData(Version, "sed-agent", 1, 5)]
    public async Task RequiresExactInformationalVersionNameSchemaAndSuccessfulHandshake(string version, string name, int schema, int exit)
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(() => FakeProcess.Json(new { schemaVersion = schema, name, version }, exit));
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_version_mismatch", error.Code);
        Assert.True(error.IsHostFault);
        Assert.Single(factory.Starts);
        Assert.False(File.Exists(fixture.Output));
    }

    [Theory]
    [InlineData(2, "invalid_request")]
    [InlineData(3, "validation_failed")]
    [InlineData(4, "conflict")]
    [InlineData(5, "io_error")]
    public async Task RetainsMatchingCliDomainFailureWithoutCallingItHostFailure(int exit, string code)
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(fixture.VersionProcess,
            () => FakeProcess.Json(new { schemaVersion = 1, error = code, message = "Synthetic refusal.", details = new { index = 3 } }, exit));
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.False(error.IsHostFault);
        Assert.Equal(exit, error.ExitCode);
        Assert.Equal(3, error.Receipt!.Value.GetProperty("details").GetProperty("index").GetInt32());
        Assert.Equal("missing", error.OutputState.Status);
    }

    [Fact]
    public async Task DomainRefusalPreservesFileThatAppearedDuringWorkerRunWithoutClaimingAuthorship()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(fixture.VersionProcess, () =>
        {
            File.WriteAllText(fixture.Output, "competing writer");
            return FakeProcess.Json(new { schemaVersion = 1, error = "conflict", message = "Output already exists." }, 4);
        });
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("conflict", error.Code);
        Assert.Equal("file", error.OutputState.Status);
        Assert.False(error.OutputState.ExistedBefore);
        Assert.Null(error.OutputState.Sha256);
        Assert.Equal("competing writer", File.ReadAllText(fixture.Output));
    }

    [Theory]
    [InlineData(4, "invalid_request")]
    [InlineData(17, "conflict")]
    [InlineData(0, "conflict")]
    public async Task RejectsDomainErrorWithWrongExitCode(int exit, string code)
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(fixture.VersionProcess,
            () => FakeProcess.Json(new { schemaVersion = 1, error = code, message = "Synthetic refusal." }, exit));
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_protocol_error", error.Code);
        Assert.True(error.IsHostFault);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}{}")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    [InlineData("{\"details\":{\"hash\":1,\"hash\":2}}")]
    public async Task RefusesAmbiguousOrMalformedReceipts(string receipt)
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(fixture.VersionProcess, () => new FakeProcess(Encoding.UTF8.GetBytes(receipt)));
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_protocol_error", error.Code);
    }

    [Fact]
    public async Task RefusesInvalidUtf8Receipt()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(fixture.VersionProcess, () => new FakeProcess([0xff, 0xfe]));
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_protocol_error", error.Code);
    }

    [Fact]
    public async Task RefusesExistingOutputWithoutStartingAnyProcess()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Output, "original");
        var factory = new FakeFactory();
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_output_exists", error.Code);
        Assert.True(error.OutputState.ExistedBefore);
        Assert.Empty(factory.Starts);
        Assert.Equal("original", File.ReadAllText(fixture.Output));
    }

    [Fact]
    public async Task AlreadyCancelledRequestStillIdentifiesPreexistingOutput()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Output, "original");
        var factory = new FakeFactory();
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, new CancellationToken(true)));
        Assert.Equal("snapshot_cancelled", error.Code);
        Assert.True(error.OutputState.ExistedBefore);
        Assert.Empty(factory.Starts);
        Assert.Equal("original", File.ReadAllText(fixture.Output));
    }

    [Fact]
    public async Task RequestBoundIsUtf8BytesAndCheckedBeforeLaunching()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory();
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, new string('é', SnapshotWorker.MaxRequestBytes / 2 + 1), fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_invalid_request", error.Code);
        Assert.False(error.IsHostFault);
        Assert.Empty(factory.Starts);
    }

    [Fact]
    public async Task InvalidRequestUnicodeIsNotSilentlyReplaced()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory();
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "\ud800", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_invalid_request", error.Code);
        Assert.Empty(factory.Starts);
    }

    [Theory]
    [InlineData(false, "snapshot_stdout_limit")]
    [InlineData(true, "snapshot_stderr_limit")]
    public async Task OversizedPipesTerminateOnlyStartedChild(bool errorPipe, string code)
    {
        using var fixture = new Fixture();
        byte[] overLimit = new byte[SnapshotWorker.MaxPipeBytes + 1];
        var child = new FakeProcess(errorPipe ? "{}"u8.ToArray() : overLimit,
            stderr: errorPipe ? overLimit : [], running: true);
        var version = fixture.VersionProcess();
        var factory = new FakeFactory(() => version, () => child);
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.True(child.Killed);
        Assert.True(child.Disposed);
        Assert.False(version.Killed);
        Assert.True(error.OutputState.ProcessExitConfirmed);
    }

    [Fact]
    public async Task WrongSuccessPathIsRefusedWithoutReadingOrDeletingThatPath()
    {
        using var fixture = new Fixture();
        string other = Path.Combine(fixture.Root, "unrelated.bin");
        File.WriteAllText(other, "unrelated");
        var factory = new FakeFactory(fixture.VersionProcess,
            () => FakeProcess.Json(new { schemaVersion = 1, written = other, outputBytes = 9, outputSha256 = Hash(Png) }));
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_protocol_error", error.Code);
        Assert.Equal("unrelated", File.ReadAllText(other));
        Assert.Equal("missing", error.OutputState.Status);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("signature")]
    public async Task MismatchingFileNeverBecomesAnImageResultAndIsPreserved(string problem)
    {
        using var fixture = new Fixture();
        byte[] image = Png.ToArray();
        if (problem == "signature") image[0] = 0;
        var factory = new FakeFactory(fixture.VersionProcess, () =>
        {
            File.WriteAllBytes(fixture.Output, image);
            return FakeProcess.Json(new { schemaVersion = 1, written = fixture.Output,
                outputBytes = image.Length + (problem == "length" ? 1 : 0),
                outputSha256 = problem == "hash" ? new string('0', 64) : Hash(image) });
        });
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_output_mismatch", error.Code);
        Assert.Equal("file", error.OutputState.Status);
        Assert.Equal(image, File.ReadAllBytes(fixture.Output));
    }

    [Fact]
    public async Task RejectsOversizedImageClaimBeforeReadingFile()
    {
        using var fixture = new Fixture();
        var factory = new FakeFactory(fixture.VersionProcess, () => FakeProcess.Json(new {
            schemaVersion = 1, written = fixture.Output, outputBytes = SnapshotWorker.MaxImageBytes + 1, outputSha256 = Hash(Png) }));
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory)
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_protocol_error", error.Code);
        Assert.Equal("missing", error.OutputState.Status);
    }

    [Fact]
    public async Task CancellationKillsOnlyActiveChildPreservesPromotedFileAndReleasesGate()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        var child = new FakeProcess([], running: true);
        var factory = new FakeFactory(fixture.VersionProcess, () =>
        {
            File.WriteAllBytes(fixture.Output, Png);
            return child;
        }, () => fixture.Success(Path.Combine(fixture.Root, "next.png")));
        var worker = fixture.Worker(factory);
        var run = worker.RunAsync(fixture.Level, "{}", fixture.Output, cancel.Token);
        await child.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => run);
        Assert.Equal("snapshot_cancelled", error.Code);
        Assert.True(child.Killed);
        Assert.Equal("file", error.OutputState.Status);
        Assert.False(error.OutputState.ExistedBefore);
        Assert.True(error.OutputState.ProcessExitConfirmed);
        Assert.Equal(Png, File.ReadAllBytes(fixture.Output));
        await worker.RunAsync(fixture.Level, "{}", Path.Combine(fixture.Root, "next.png"), CancellationToken.None);
        Assert.Equal(3, factory.Starts.Count);
    }

    [Fact]
    public async Task CancellingQueuedCallDoesNotKillTheAlreadyRunningChild()
    {
        using var fixture = new Fixture();
        using var firstCancel = new CancellationTokenSource();
        using var queuedCancel = new CancellationTokenSource();
        var child = new FakeProcess([], running: true);
        var factory = new FakeFactory(fixture.VersionProcess, () => child);
        var worker = fixture.Worker(factory);
        var first = worker.RunAsync(fixture.Level, "{}", fixture.Output, firstCancel.Token);
        await child.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = worker.RunAsync(fixture.Level, "{}", Path.Combine(fixture.Root, "queued.png"), queuedCancel.Token);
        queuedCancel.Cancel();
        var queuedError = await Assert.ThrowsAsync<SnapshotWorkerException>(() => queued);
        Assert.Equal("snapshot_cancelled", queuedError.Code);
        Assert.False(child.Killed);
        Assert.False(child.Disposed);
        Assert.Equal(2, factory.Starts.Count);
        firstCancel.Cancel();
        var firstError = await Assert.ThrowsAsync<SnapshotWorkerException>(() => first);
        Assert.Equal("snapshot_cancelled", firstError.Code);
        Assert.True(child.Killed);
    }

    [Fact]
    public async Task DeadlineKillsHungChildAndReportsTimeout()
    {
        using var fixture = new Fixture();
        var child = new FakeProcess([], running: true);
        var factory = new FakeFactory(fixture.VersionProcess, () => child);
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => fixture.Worker(factory, TimeSpan.FromMilliseconds(500))
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_timeout", error.Code);
        Assert.True(child.Killed);
        Assert.True(error.OutputState.ProcessExitConfirmed);
    }

    [Fact]
    public async Task UnconfirmedTerminationDoesNotClaimChildHasStopped()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        var child = new FakeProcess([], running: true) { CannotConfirmExitAfterKill = true };
        var factory = new FakeFactory(fixture.VersionProcess, () => child);
        var worker = fixture.Worker(factory);
        var run = worker.RunAsync(fixture.Level, "{}", fixture.Output, cancel.Token);
        await child.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var error = await Assert.ThrowsAsync<SnapshotWorkerException>(() => run);
        Assert.Equal("snapshot_termination_unconfirmed", error.Code);
        Assert.False(error.OutputState.ProcessExitConfirmed);
        Assert.True(child.Killed);
        Assert.True(child.Disposed);
        var next = await Assert.ThrowsAsync<SnapshotWorkerException>(() => worker
            .RunAsync(fixture.Level, "{}", fixture.Output, CancellationToken.None));
        Assert.Equal("snapshot_worker_unavailable", next.Code);
        Assert.False(next.OutputState.ProcessExitConfirmed);
        Assert.Equal(2, factory.Starts.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(181)]
    public void RefusesUnboundedOrNonpositiveTimeout(int seconds)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Worker(new FakeFactory(), TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65537)]
    public async Task CapturedIdentityHashMatchesExactBytesAcrossChunkBoundaries(int length)
    {
        byte[] content = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        using var stream = new MemoryStream(content);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)),
            await SnapshotWorker.HashCapturedLengthAsync(stream, content.Length, CancellationToken.None));
    }

    [Fact]
    public async Task GrowingIdentityStreamConsumesOnlyCapturedLengthPlusOneProbe()
    {
        using var stream = new GrowingIdentityStream();
        var error = await Assert.ThrowsAsync<IOException>(() => SnapshotWorker.HashCapturedLengthAsync(stream, 11, CancellationToken.None));
        Assert.Contains("changed size", error.Message);
        Assert.Equal(12, stream.BytesRead);
        Assert.Equal(new[] { 11, 1 }, stream.RequestedReads);
    }

    [Fact]
    public async Task ShrinkingIdentityStreamIsRejectedInsteadOfHashingItsPrefix()
    {
        using var stream = new MemoryStream(new byte[3]);
        var error = await Assert.ThrowsAsync<IOException>(() => SnapshotWorker.HashCapturedLengthAsync(stream, 5, CancellationToken.None));
        Assert.Contains("shrank", error.Message);
    }

    [Fact]
    public async Task IdentityHashObservesCancellationBeforeConsumingContent()
    {
        using var stream = new GrowingIdentityStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SnapshotWorker.HashCapturedLengthAsync(stream, 11, new CancellationToken(true)));
        Assert.Equal(0, stream.BytesRead);
    }

    private sealed class GrowingIdentityStream : Stream
    {
        public long BytesRead { get; private set; }
        public List<int> RequestedReads { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => BytesRead + 4096;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            RequestedReads.Add(buffer.Length);
            buffer.Span.Fill(0x47);
            BytesRead += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "SED Snapshot Test " + Guid.NewGuid().ToString("N"));
        public string Agent => Path.Combine(Root, "sed-agent.exe");
        public string Level => Path.Combine(Root, "test level.jkl");
        public string Output => Path.Combine(Root, "test image.png");
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            foreach (string name in new[] { "sed-agent.exe", "sed-agent.dll", "Sed.Core.dll", "Sed.Formats.dll", "Sed.Rendering.dll", "Sed.Rendering.Vulkan.dll" })
                File.WriteAllText(Path.Combine(Root, name), "Original synthetic component " + name);
            File.WriteAllText(Level, "Original synthetic level placeholder");
        }
        public SnapshotWorker Worker(FakeFactory factory, TimeSpan? timeout = null) => new(Agent, Version, timeout, factory);
        public FakeProcess VersionProcess() => FakeProcess.Json(new { schemaVersion = 1, name = "sed-agent", version = Version });
        public FakeProcess Success(string? output = null)
        {
            output ??= Output;
            File.WriteAllBytes(output, Png);
            return FakeProcess.Json(new { schemaVersion = 1, written = output, outputBytes = Png.Length, outputSha256 = Hash(Png) },
                stderr: "renderer diagnostic");
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeFactory(params Func<FakeProcess>[] processes) : ISnapshotProcessFactory
    {
        private readonly Queue<Func<FakeProcess>> _processes = new(processes);
        public List<ProcessStartInfo> Starts { get; } = [];
        public ISnapshotProcess Start(ProcessStartInfo start)
        {
            Starts.Add(start);
            Assert.NotEmpty(_processes);
            return _processes.Dequeue()();
        }
    }

    private sealed class FakeProcess : ISnapshotProcess
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MemoryStream Input { get; } = new();
        public Stream StandardInput => Input;
        public Stream StandardOutput { get; }
        public Stream StandardError { get; }
        public int ExitCode { get; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public bool CannotConfirmExitAfterKill { get; init; }
        public FakeProcess(byte[] stdout, int exitCode = 0, byte[]? stderr = null, bool running = false)
        {
            StandardOutput = new MemoryStream(stdout, writable: false);
            StandardError = new MemoryStream(stderr ?? [], writable: false);
            ExitCode = exitCode;
            if (!running) _exit.SetResult();
        }
        public static FakeProcess Json(object receipt, int exit = 0, string stderr = "") =>
            new(JsonSerializer.SerializeToUtf8Bytes(receipt), exit, Encoding.UTF8.GetBytes(stderr));
        public Task WaitForExitAsync(CancellationToken token)
        {
            WaitStarted.TrySetResult();
            if (Killed && CannotConfirmExitAfterKill) throw new InvalidOperationException("Synthetic inability to inspect child exit.");
            return _exit.Task.WaitAsync(token);
        }
        public void KillTree() { Killed = true; _exit.TrySetResult(); }
        public void Dispose()
        {
            Disposed = true;
            StandardOutput.Dispose(); StandardError.Dispose(); Input.Dispose();
        }
    }
}

using System.Text;
using System.Text.Json.Nodes;
using Sed.Core.IO;
using Sed.Formats.Automation;
using Sed.Mcp.Infrastructure;

namespace Sed.Mcp.Tests;

public sealed class OperationStoreTests : IDisposable
{
    private readonly McpPolicyTestDirectory _files = new();
    private const string OutputText = "original synthetic reviewed output\n";
    private static readonly string ExpectedHash = AgentWorkspace.Hash(Encoding.UTF8.GetBytes(OutputText));
    private McpFilePolicy Policy => new(_files.Workspace);
    private OperationStore Store() => new(Policy);
    private OperationIntent Intent(string output = "output.jkl") => new("new", null, null, null,
        _files.PathIn("workspace/" + output), new string('a', 64), ExpectedHash);
    private static string Id() => Guid.NewGuid().ToString("N");
    private string ReceiptPath(string id) => _files.PathIn("workspace/.sed-mcp/receipts/" + id + ".json");
    private static void Produce(OperationIntent intent, string text = OutputText) =>
        File.WriteAllText(intent.OutputPath, text, new UTF8Encoding(false));

    [Fact]
    public void BeginPersistsIntentBeforeAnyOutputAndSameIdDoesNotCreateAgain()
    {
        var store = Store(); var intent = Intent(); string id = Id();
        var first = store.Begin(id, intent);
        Assert.True(first.Created);
        Assert.True(File.Exists(ReceiptPath(id)));
        Assert.False(File.Exists(intent.OutputPath));
        var observation = Assert.IsType<OperationObservation>(store.Get(id));
        Assert.Equal(OperationState.Running, observation.ObservedState);
        Assert.True(observation.OriginRunning);
        Assert.False(observation.OutputExists);
        Assert.False(store.Begin(Guid.Parse(id).ToString("D").ToUpperInvariant(), intent).Created);
        Assert.Equal(first.Receipt, store.Get(id)!.Receipt);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("expected")]
    [InlineData("command")]
    [InlineData("output")]
    [InlineData("input")]
    [InlineData("inputHash")]
    [InlineData("revision")]
    public void OneIdCannotBeReboundToDifferentImmutableIntent(string field)
    {
        var intent = Intent() with { InputPath = _files.Write("workspace/input.jkl", "input"),
            InputSha256 = new string('b', 64), SourceRevision = new string('c', 64) };
        var changed = field switch
        {
            "request" => intent with { RequestSha256 = new string('d', 64) },
            "expected" => intent with { ExpectedOutputSha256 = new string('d', 64) },
            "command" => intent with { Command = "convert" },
            "output" => intent with { OutputPath = _files.PathIn("workspace/other.jkl") },
            "input" => intent with { InputPath = _files.PathIn("workspace/other.jkl") },
            "inputHash" => intent with { InputSha256 = new string('d', 64) },
            _ => intent with { SourceRevision = new string('d', 64) },
        };
        var store = Store(); string id = Id();
        store.Begin(id, intent);
        byte[] before = File.ReadAllBytes(ReceiptPath(id));
        Assert.Throws<AgentConflictException>(() => store.Begin(id, changed));
        Assert.Equal(before, File.ReadAllBytes(ReceiptPath(id)));
        Assert.False(File.Exists(intent.OutputPath));
    }

    [Fact]
    public void IntentHashAndIdNormalizationDoNotFabricateNewOperations()
    {
        var store = Store(); string id = Id(); var intent = Intent();
        store.Begin(id.ToUpperInvariant(), intent with { RequestSha256 = intent.RequestSha256.ToUpperInvariant(),
            ExpectedOutputSha256 = intent.ExpectedOutputSha256.ToUpperInvariant() });
        Assert.False(store.Begin(id, intent).Created);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(ReceiptPath(id))!, "*.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-operation-id")]
    [InlineData("../../receipt")]
    [InlineData(" 1d2cd0abf12547619e261a10133e4550")]
    public void InvalidIdsDoNotCreateReceipts(string id)
    {
        var store = Store();
        Assert.Throws<AgentRequestException>(() => store.Begin(id, Intent()));
        Assert.Throws<AgentRequestException>(() => store.Get(id));
        Assert.Empty(Directory.GetFiles(_files.PathIn("workspace/.sed-mcp/receipts")));
    }

    [Fact]
    public void InvalidIntentNeverCreatesHistory()
    {
        var store = Store();
        var intent = Intent();
        var invalid = new[]
        {
            intent with { Command = "run shell" },
            intent with { InputSha256 = new string('a', 64) },
            intent with { SourceRevision = new string('a', 64) },
            intent with { InputPath = _files.PathIn("workspace/input.jkl") },
            intent with { RequestSha256 = "short" },
            intent with { ExpectedOutputSha256 = new string('g', 64) },
            intent with { InputPath = intent.OutputPath, InputSha256 = ExpectedHash },
        };
        foreach (var candidate in invalid) Assert.Throws<AgentRequestException>(() => store.Begin(Id(), candidate));
        Assert.Empty(Directory.GetFiles(_files.PathIn("workspace/.sed-mcp/receipts")));
    }

    [Fact]
    public void PreexistingMatchingOutputCannotBeClaimedAsANewOperation()
    {
        var intent = Intent(); Produce(intent);
        var store = Store(); string id = Id();
        Assert.Throws<AgentRequestException>(() => store.Begin(id, intent));
        Assert.Null(store.Get(id));
        Assert.Equal(OutputText, File.ReadAllText(intent.OutputPath));
    }

    [Fact]
    public void CreatorCanCommitOnlyMatchingOutputAndTerminalStateCannotBeReplaced()
    {
        var store = Store(); var intent = Intent(); string id = Id();
        store.Begin(id, intent);
        Assert.Throws<AgentConflictException>(() => store.Complete(id));
        Produce(intent, "wrong output");
        Assert.Throws<AgentConflictException>(() => store.Complete(id));
        Produce(intent);
        var committed = store.Complete(id, "promoted before reply delivery");
        Assert.Equal(OperationState.Committed, committed.State);
        Assert.Equal(committed, store.Complete(id));
        Assert.Throws<AgentConflictException>(() => store.Fail(id, "late protocol failure"));
        Assert.Throws<AgentConflictException>(() => store.MarkUnknown(id, "late cancellation"));
        Assert.False(store.Begin(id, intent).Created);
        Assert.Equal(OperationState.Committed, Store().Get(id)!.ObservedState);
    }

    [Fact]
    public void OtherStoreCannotClaimCompletionEvenWhenBytesMatch()
    {
        var owner = Store(); var intent = Intent(); string id = Id();
        owner.Begin(id, intent); Produce(intent);
        var observer = Store();
        Assert.False(observer.Begin(id, intent).Created);
        Assert.Throws<AgentConflictException>(() => observer.Complete(id));
        Assert.Throws<AgentConflictException>(() => observer.Fail(id, "not the creator"));
        Assert.Equal(OperationState.Running, owner.Get(id)!.ObservedState);
        Assert.Equal(OperationState.Unknown, observer.Get(id)!.ObservedState);
    }

    [Theory]
    [InlineData(OperationState.Committed)]
    [InlineData(OperationState.Failed)]
    [InlineData(OperationState.Unknown)]
    public void TerminalReplayFromAnotherStoreCannotChangeHistoryAndDoesNotRetainCreatorLease(OperationState state)
    {
        var owner = Store(); var intent = Intent(); string id = Id();
        owner.Begin(id, intent);
        Assert.Equal(1, owner.ActiveLeaseCount);
        if (state == OperationState.Committed) Produce(intent);
        static OperationReceipt Finish(OperationStore store, string id, OperationState state, string message) => state switch
        {
            OperationState.Committed => store.Complete(id, message),
            OperationState.Failed => store.Fail(id, message),
            _ => store.MarkUnknown(id, message),
        };
        var original = Finish(owner, id, state, "creator's recorded evidence");
        Assert.Equal(0, owner.ActiveLeaseCount);
        byte[] originalBytes = File.ReadAllBytes(ReceiptPath(id));
        var observer = Store();
        var replay = Finish(observer, id, state, "different caller's proposed replacement");
        Assert.Equal(original, replay);
        Assert.Equal(originalBytes, File.ReadAllBytes(ReceiptPath(id)));
        Assert.Equal(0, observer.ActiveLeaseCount);
        OperationState conflicting = state == OperationState.Committed ? OperationState.Failed : OperationState.Committed;
        Assert.Throws<AgentConflictException>(() => Finish(observer, id, conflicting, "cannot rewrite history"));
        Assert.Equal(originalBytes, File.ReadAllBytes(ReceiptPath(id)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartReconcilesUnfinishedWorkAsUnknownWithSeparateOutputEvidence(bool promotedBeforeCrash)
    {
        var abandoned = Store(); var intent = Intent(); string id = Id();
        abandoned.Begin(id, intent);
        if (promotedBeforeCrash) Produce(intent);
        byte[] originalReceipt = File.ReadAllBytes(ReceiptPath(id));
        var restarted = Store();
        var observation = restarted.Get(id)!;
        Assert.Equal(OperationState.Running, observation.Receipt.State);
        Assert.Equal(OperationState.Unknown, observation.ObservedState);
        Assert.True(observation.OriginRunning);
        Assert.Equal(promotedBeforeCrash, observation.OutputExists);
        Assert.Equal(promotedBeforeCrash, observation.MatchesExpectedOutput);
        Assert.Equal(originalReceipt, File.ReadAllBytes(ReceiptPath(id)));
        Assert.False(restarted.Begin(id, intent).Created);
        Assert.Throws<AgentConflictException>(() => restarted.Complete(id));
    }

    [Fact]
    public void HistoricalCommitSurvivesOutputDriftAndInputRemoval()
    {
        var store = Store(); string id = Id();
        string input = _files.Write("workspace/old/input.jkl", "source");
        var intent = Intent() with { InputPath = input, InputSha256 = new string('d', 64) };
        store.Begin(id, intent); Produce(intent); var receipt = store.Complete(id);
        File.Delete(input); Directory.Delete(Path.GetDirectoryName(input)!);
        Produce(intent, "changed later");
        var changed = Store().Get(id)!;
        Assert.Equal(receipt, changed.Receipt);
        Assert.Equal(OperationState.Committed, changed.ObservedState);
        Assert.True(changed.OutputExists);
        Assert.False(changed.MatchesExpectedOutput);
        File.Delete(intent.OutputPath);
        var missing = Store().Get(id)!;
        Assert.Equal(OperationState.Committed, missing.ObservedState);
        Assert.False(missing.OutputExists);
        Assert.Equal(receipt, missing.Receipt);
    }

    [Fact]
    public async Task ConcurrentSameIdCreatesExactlyOneIntentReceipt()
    {
        string id = Id(); var intent = Intent();
        var stores = Enumerable.Range(0, 12).Select(_ => Store()).ToArray();
        var attempts = await Task.WhenAll(stores.Select(store => Task.Run(() => store.Begin(id, intent))));
        Assert.Single(attempts, result => result.Created);
        Assert.All(attempts, result => Assert.Equal(attempts[0].Receipt, result.Receipt));
        Assert.Single(Directory.GetFiles(_files.PathIn("workspace/.sed-mcp/receipts"), "*.json"));
        Assert.Empty(Directory.GetFiles(_files.PathIn("workspace/.sed-mcp/receipts"), "*.tmp"));
        Assert.False(File.Exists(intent.OutputPath));
    }

    [Fact]
    public async Task CompetingNoOverwritePromotionsKeepTheLosingOutcomeFailedEvenWithMatchingBytes()
    {
        var intent = Intent(); var first = Store(); var second = Store(); string firstId = Id(), secondId = Id();
        first.Begin(firstId, intent); second.Begin(secondId, intent with { RequestSha256 = new string('b', 64) });
        var attempts = new[] { (Store: first, Id: firstId), (Store: second, Id: secondId) };
        using var start = new Barrier(attempts.Length);
        await Task.WhenAll(attempts.Select(attempt => Task.Run(() =>
        {
            string temporary = _files.Write("workspace/" + attempt.Id + ".tmp", OutputText);
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            try
            {
                AtomicFilePromotion.MoveNew(temporary, intent.OutputPath);
                attempt.Store.Complete(attempt.Id);
            }
            catch (IOException)
            {
                attempt.Store.Fail(attempt.Id, "Another operation occupied the output.");
                File.Delete(temporary);
            }
        })));
        var observations = attempts.Select(attempt => Store().Get(attempt.Id)!).ToArray();
        Assert.Single(observations, item => item.ObservedState == OperationState.Committed);
        Assert.Single(observations, item => item.ObservedState == OperationState.Failed);
        Assert.All(observations, item => Assert.True(item.MatchesExpectedOutput));
        Assert.Equal(OutputText, File.ReadAllText(intent.OutputPath));
    }

    [Theory]
    [InlineData(OperationState.Failed)]
    [InlineData(OperationState.Unknown)]
    public void NoncommitOutcomeIsDurableAndNeverReclassifiedFromFileHashes(OperationState state)
    {
        var store = Store(); var intent = Intent(); string id = Id();
        store.Begin(id, intent);
        if (state == OperationState.Failed) store.Fail(id, "canceled before promotion");
        else store.MarkUnknown(id, "promotion outcome not observed");
        Produce(intent); // unrelated identical bytes do not repair the history
        var observation = Store().Get(id)!;
        Assert.Equal(state, observation.ObservedState);
        Assert.True(observation.MatchesExpectedOutput);
        Assert.False(Store().Begin(id, intent).Created);
        Assert.Throws<AgentConflictException>(() => store.Complete(id));
    }

    [Fact]
    public void OutputReparseReplacementPreservesHistoryAndReportsInspectionFailure()
    {
        string folder = _files.PathIn("workspace/sub"); Directory.CreateDirectory(folder);
        var store = Store(); var intent = Intent("sub/output.jkl"); string id = Id();
        store.Begin(id, intent); Produce(intent); var receipt = store.Complete(id);
        File.Delete(intent.OutputPath); Directory.Delete(folder);
        _files.CreateLinkedDirectory("workspace/sub", _files.Resources);
        _files.Write("resources/output.jkl", OutputText);
        var observation = Store().Get(id)!;
        Assert.Equal(receipt, observation.Receipt);
        Assert.Equal(OperationState.Committed, observation.ObservedState);
        Assert.Null(observation.OutputExists);
        Assert.Null(observation.ObservedOutputSha256);
        Assert.NotNull(observation.OutputInspectionError);
    }

    [Fact]
    public void OversizeOutputReturnsBoundedInspectionEvidence()
    {
        var store = Store(); var intent = Intent(); string id = Id();
        store.Begin(id, intent);
        using (var stream = File.Create(intent.OutputPath)) stream.SetLength(AgentWorkspace.MaxLevelBytes + 1);
        var observation = store.Get(id)!;
        Assert.True(observation.OutputExists);
        Assert.Null(observation.ObservedOutputSha256);
        Assert.Contains("128 MiB", observation.OutputInspectionError);
        Assert.Throws<AgentConflictException>(() => store.Complete(id));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("truncated")]
    [InlineData("schema")]
    [InlineData("state")]
    [InlineData("integerState")]
    [InlineData("lease")]
    [InlineData("operationId")]
    [InlineData("command")]
    [InlineData("missingCommand")]
    [InlineData("unexpectedProperty")]
    [InlineData("inputHash")]
    [InlineData("requestHash")]
    [InlineData("outputHash")]
    [InlineData("outputPath")]
    [InlineData("timestamp")]
    [InlineData("duplicateProperty")]
    public void MalformedReceiptNeverGetsRewrittenOrAcceptedAsAReplay(string corruption)
    {
        var store = Store(); var intent = Intent(); string id = Id();
        store.Begin(id, intent); string path = ReceiptPath(id);
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        switch (corruption)
        {
            case "schema": json["schemaVersion"] = 99; break;
            case "state": json["receipt"]!["state"] = "invented"; break;
            case "integerState": json["receipt"]!["state"] = 1; break;
            case "lease": json["lease"] = "invalid"; break;
            case "operationId": json["receipt"]!["operationId"] = Id(); break;
            case "command": json["receipt"]!["intent"]!["command"] = ""; break;
            case "missingCommand": json["receipt"]!["intent"]!.AsObject().Remove("command"); break;
            case "unexpectedProperty": json["receipt"]!["intent"]!["execute"] = true; break;
            case "inputHash": json["receipt"]!["intent"]!["inputSha256"] = "invalid"; break;
            case "requestHash": json["receipt"]!["intent"]!["requestSha256"] = "invalid"; break;
            case "outputHash": json["receipt"]!["intent"]!["expectedOutputSha256"] = "invalid"; break;
            case "outputPath": json["receipt"]!["intent"]!["outputPath"] = "relative.jkl"; break;
            case "timestamp": json["receipt"]!["updatedUtc"] = "0001-01-01T00:00:00Z"; break;
        }
        string changed = corruption switch
        {
            "malformed" => "not json",
            "truncated" => "{\"schemaVersion\":1,",
            "duplicateProperty" => json.ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            _ => json.ToJsonString(),
        };
        File.WriteAllText(path, changed);
        byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => Store().Get(id));
        Assert.Throws<InvalidOperationException>(() => store.Begin(id, intent));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(intent.OutputPath));
    }

    [Fact]
    public void OversizeReceiptIsRefusedWithoutDeserializingIt()
    {
        var store = Store(); string id = Id(); store.Begin(id, Intent());
        string path = ReceiptPath(id);
        using (var stream = File.OpenWrite(path)) stream.SetLength(64 * 1024 + 1);
        Assert.Throws<InvalidOperationException>(() => Store().Get(id));
        Assert.Equal(64 * 1024 + 1, new FileInfo(path).Length);
    }

    public void Dispose() => _files.Dispose();
}

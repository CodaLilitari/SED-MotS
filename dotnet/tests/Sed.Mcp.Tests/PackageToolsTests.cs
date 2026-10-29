using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;

namespace Sed.Mcp.Tests;

/// <summary>Original package fixtures exercise real shared planning, publication and durable MCP recovery.</summary>
public sealed class PackageToolsTests
{
    [Fact]
    public async Task DedicatedPackageToolsAndFocusedSchemaAreDiscoverable()
    {
        using var fixture = new Fixture();
        using var tools = fixture.Tools();
        var discovery = Success(await tools.CallAsync("sed_discover", null));
        var names = discovery.GetProperty("tools").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("sed_package_plan", names);
        Assert.Contains("sed_package_apply", names);
        var schema = Success(await tools.CallAsync("sed_discover", Args(new { topic = "package" })));
        var properties = schema.GetProperty("properties");
        foreach (string field in new[] { "inputSha256", "resourceArchives", "stockArchives", "levelFileName" })
            Assert.True(properties.TryGetProperty(field, out _), field);
        Assert.False(properties.TryGetProperty("operations", out _));
        Assert.False(properties.TryGetProperty("files", out _));
        Assert.True(ToolCatalog.Tools.Single(t => t.Name == "sed_package_plan").Annotations?.ReadOnlyHint);
        Assert.False(ToolCatalog.Tools.Single(t => t.Name == "sed_package_apply").Annotations?.ReadOnlyHint);
        Assert.Empty(fixture.Receipts());
        Assert.False(File.Exists(fixture.Options.AgentPath));
    }

    [Fact]
    public void CliPackageCommandsExposeBothMandatoryReviewIdentities()
    {
        var commands = AgentCommandCatalog.Commands();
        var plan = Assert.Single(commands, c => c.Name == "package-plan");
        var apply = Assert.Single(commands, c => c.Name == "package-apply");
        Assert.False(plan.WritesFiles);
        Assert.True(apply.WritesFiles);
        var required = JsonSerializer.SerializeToElement(apply.ArgumentsJsonSchema)
            .GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("expectedOutputSha256", required);
        Assert.Contains("expectedManifestSha256", required);
    }

    [Theory]
    [InlineData("expectedOutputSha256")]
    [InlineData("expectedManifestSha256")]
    [InlineData("operationId")]
    public async Task ApplyRequiresEveryCommitIdentityBeforeReadingSourceOrCreatingHistory(string missing)
    {
        using var fixture = new Fixture();
        using var tools = fixture.Tools();
        var arguments = Args(new {
            level = "absent.jkl", request = new { schemaVersion = 1, inputSha256 = new string('0', 64), levelFileName = "original.jkl" },
            output = "package.goo", expectedOutputSha256 = new string('1', 64),
            expectedManifestSha256 = new string('2', 64), operationId = Guid.NewGuid().ToString()
        });
        arguments.Remove(missing);
        var failure = Failure(await tools.CallAsync("sed_package_apply", arguments), "invalid_request");
        Assert.Contains(missing, failure.GetProperty("message").GetString());
        Assert.Empty(fixture.Receipts());
        Assert.False(File.Exists(fixture.PathFor("package.goo")));
    }

    [Theory]
    [InlineData(ProjectType.JediKnight, ".gob")]
    [InlineData(ProjectType.MysteriesOfTheSith, ".goo")]
    public async Task ReviewedPackageCommitCreatesTheExactPlannedArchiveAndDurableOutcome(ProjectType game, string extension)
    {
        using var fixture = new Fixture();
        string input = fixture.Room(game);
        byte[] original = File.ReadAllBytes(input);
        var request = fixture.Request();
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_package_plan", Args(new { level = input, request })));
        Assert.True(plan.GetProperty("canBuild").GetBoolean(), plan.GetRawText());
        string output = "reviewed" + extension;
        Assert.False(File.Exists(fixture.PathFor(output)));
        Assert.Empty(fixture.Receipts());
        string id = Guid.NewGuid().ToString();
        var arguments = CommitArgs(input, request, output, plan, id);
        var committed = Success(await tools.CallAsync("sed_package_apply", arguments));
        string expected = plan.GetProperty("outputSha256").GetString()!;
        Assert.Equal(expected, AgentWorkspace.Hash(File.ReadAllBytes(fixture.PathFor(output))));
        Assert.Equal(original, File.ReadAllBytes(input));
        using var archive = GobArchive.Open(fixture.PathFor(output));
        var level = Assert.Single(archive.Entries, e => e.NormalizedName == "jkl/original.jkl");
        Assert.Equal(original, archive.ReadBytes(level));
        Assert.Single(archive.Entries, e => e.NormalizedName == "episode.jk");
        var outcome = committed.GetProperty("outcome");
        Assert.Equal("committed", outcome.GetProperty("observedState").GetString());
        Assert.True(outcome.GetProperty("matchesExpectedOutput").GetBoolean());
        Assert.Empty(Directory.GetFiles(fixture.Root, ".sed-*.tmp"));
    }

    [Fact]
    public async Task ReconnectReplayReturnsHistoryAfterSourceAndArchivesDisappear()
    {
        using var fixture = new Fixture();
        string input = fixture.Room(), archive = fixture.Archive("original unused resource");
        var request = fixture.Request(archive);
        string id = Guid.NewGuid().ToString();
        Dictionary<string, JsonElement> arguments;
        byte[] created;
        using (var tools = fixture.Tools())
        {
            var plan = Success(await tools.CallAsync("sed_package_plan", Args(new { level = input, request })));
            arguments = CommitArgs(input, request, "reviewed.goo", plan, id);
            Success(await tools.CallAsync("sed_package_apply", arguments));
            created = File.ReadAllBytes(fixture.PathFor("reviewed.goo"));
        }
        File.Delete(input);
        File.Delete(archive);
        using var reconnected = fixture.Tools();
        var replay = Success(await reconnected.CallAsync("sed_package_apply", arguments));
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal("committed", replay.GetProperty("outcome").GetProperty("observedState").GetString());
        Assert.Equal(created, File.ReadAllBytes(fixture.PathFor("reviewed.goo")));
        Assert.Single(fixture.Receipts());
        arguments["expectedManifestSha256"] = JsonSerializer.SerializeToElement(new string('0', 64));
        Failure(await reconnected.CallAsync("sed_package_apply", arguments), "conflict");
        var outcome = Success(await reconnected.CallAsync("sed_outcome", Args(new { operationId = id })));
        Assert.True(outcome.GetProperty("outcome").GetProperty("matchesExpectedOutput").GetBoolean());
    }

    [Theory]
    [InlineData("expectedOutputSha256")]
    [InlineData("expectedManifestSha256")]
    public async Task StaleReviewedIdentityRefusesBeforeWritingOrStartingOperation(string field)
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        var request = fixture.Request();
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_package_plan", Args(new { level = input, request })));
        var arguments = CommitArgs(input, request, "refused.goo", plan, Guid.NewGuid().ToString());
        arguments[field] = JsonSerializer.SerializeToElement(new string('0', 64));
        Failure(await tools.CallAsync("sed_package_apply", arguments), "conflict");
        Assert.False(File.Exists(fixture.PathFor("refused.goo")));
        Assert.Empty(fixture.Receipts());
    }

    [Fact]
    public async Task ChangedManifestCannotReuseReviewEvenWhenTheArchiveOutputIsUnchanged()
    {
        using var fixture = new Fixture();
        string input = fixture.Room(), archive = fixture.Archive("first unused resource");
        using var tools = fixture.Tools();
        var firstRequest = fixture.Request(archive);
        var first = Success(await tools.CallAsync("sed_package_plan", Args(new { level = input, request = firstRequest })));
        fixture.Archive("different unused resource");
        var currentRequest = fixture.Request(archive);
        var current = Success(await tools.CallAsync("sed_package_plan", Args(new { level = input, request = currentRequest })));
        Assert.Equal(first.GetProperty("outputSha256").GetString(), current.GetProperty("outputSha256").GetString());
        Assert.NotEqual(first.GetProperty("manifestSha256").GetString(), current.GetProperty("manifestSha256").GetString());
        Failure(await tools.CallAsync("sed_package_apply",
            CommitArgs(input, currentRequest, "refused.goo", first, Guid.NewGuid().ToString())), "conflict");
        Assert.False(File.Exists(fixture.PathFor("refused.goo")));
        Assert.Empty(fixture.Receipts());
    }

    [Fact]
    public async Task BlockedPlanCanBeReviewedButCannotCreateAnOperation()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        var request = fixture.Request();
        request["extraResources"] = new[] { new { name = "absent.wav", folder = "sound" } };
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_package_plan", Args(new { level = input, request })));
        Assert.False(plan.GetProperty("canBuild").GetBoolean());
        Assert.NotEmpty(plan.GetProperty("issues").EnumerateArray());
        var arguments = CommitArgs(input, request, "refused.goo", plan, Guid.NewGuid().ToString());
        arguments["expectedOutputSha256"] = JsonSerializer.SerializeToElement(new string('0', 64));
        Failure(await tools.CallAsync("sed_package_apply", arguments), "validation_failed");
        Assert.False(File.Exists(fixture.PathFor("refused.goo")));
        Assert.Empty(fixture.Receipts());
    }

    [Fact]
    public async Task ExternalArchiveIsDeniedWithoutReadingOrDisclosingItsContents()
    {
        using var fixture = new Fixture();
        using var outside = new Fixture();
        string input = fixture.Room();
        string privateFile = outside.PathFor("private.goo");
        const string privateText = "original private fixture which is not an archive";
        File.WriteAllText(privateFile, privateText);
        var request = fixture.Request();
        request["resourceArchives"] = new[] { new { path = privateFile, sha256 = new string('0', 64) } };
        using var tools = fixture.Tools();
        var failure = Failure(await tools.CallAsync("sed_package_plan", Args(new { level = input, request })), "invalid_request");
        Assert.StartsWith("MCP file policy: Read path is outside", failure.GetProperty("message").GetString());
        Assert.DoesNotContain(privateText, failure.GetRawText());
        Assert.Empty(fixture.Receipts());
    }

    [Theory]
    [InlineData(".jed")]
    [InlineData(".sed")]
    [InlineData(".ned")]
    public async Task NativePackageInputRequiresAReviewedJklConversion(string extension)
    {
        using var fixture = new Fixture();
        string input = fixture.PathFor("native" + extension);
        File.WriteAllText(input, "original invalid native fixture; must not be imported for packaging");
        using var tools = fixture.Tools();
        var failure = Failure(await tools.CallAsync("sed_package_plan", Args(new { level = input,
            request = new { schemaVersion = 1, inputSha256 = AgentWorkspace.Hash(File.ReadAllBytes(input)), levelFileName = "original.jkl" } })), "invalid_request");
        Assert.Contains("JKL", failure.GetProperty("message").GetString());
        Assert.Empty(fixture.Receipts());
    }

    [Fact]
    public async Task PackageOutputMustRemainInsideWorkspaceWithThePlannedGameExtension()
    {
        using var fixture = new Fixture();
        using var outside = new Fixture();
        string input = fixture.Room();
        var request = fixture.Request();
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_package_plan", Args(new { level = input, request })));
        var external = Failure(await tools.CallAsync("sed_package_apply",
            CommitArgs(input, request, outside.PathFor("refused.goo"), plan, Guid.NewGuid().ToString())), "invalid_request");
        Assert.StartsWith("MCP file policy: Outputs must be files inside", external.GetProperty("message").GetString());
        Failure(await tools.CallAsync("sed_package_apply",
            CommitArgs(input, request, "wrong-game.gob", plan, Guid.NewGuid().ToString())), "invalid_request");
        Assert.False(File.Exists(outside.PathFor("refused.goo")));
        Assert.False(File.Exists(fixture.PathFor("wrong-game.gob")));
        Assert.Empty(fixture.Receipts());
    }

    [Fact]
    public async Task PackageCannotOverwriteAnExistingFileOrClaimItsMatchingBytes()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        var request = fixture.Request();
        using var tools = fixture.Tools();
        var plan = Success(await tools.CallAsync("sed_package_plan", Args(new { level = input, request })));
        Success(await tools.CallAsync("sed_package_apply",
            CommitArgs(input, request, "reviewed.goo", plan, Guid.NewGuid().ToString())));
        byte[] original = File.ReadAllBytes(fixture.PathFor("reviewed.goo"));
        Failure(await tools.CallAsync("sed_package_apply",
            CommitArgs(input, request, "reviewed.goo", plan, Guid.NewGuid().ToString())), "invalid_request");
        Assert.Equal(original, File.ReadAllBytes(fixture.PathFor("reviewed.goo")));
        Assert.Single(fixture.Receipts());
    }

    [Fact]
    public async Task OversizedReviewRefusesBeforeOutputOrDurableIntent()
    {
        using var fixture = new Fixture();
        string input = fixture.Room(), archive = fixture.LargeReviewArchive();
        var request = fixture.Request(archive);
        request["extraResources"] = new[] { new { name = "review.3do", folder = "3do" } };
        var prepared = AgentWorkspace.PlanPackage(input, JsonSerializer.Serialize(request, AgentWorkspace.JsonOptions));
        Assert.True(prepared.CanBuild, prepared.Report.GetProperty("issues").GetRawText());
        Assert.True(Encoding.UTF8.GetByteCount(prepared.Report.GetRawText()) > SedTools.MaxResultBytes);
        using var tools = fixture.Tools();
        Failure(await tools.CallAsync("sed_package_plan", Args(new { level = input, request })), "invalid_request");
        Failure(await tools.CallAsync("sed_package_apply",
            CommitArgs(input, request, "oversized-review.goo", prepared.Report, Guid.NewGuid().ToString())), "invalid_request");
        Assert.False(File.Exists(fixture.PathFor("oversized-review.goo")));
        Assert.Empty(fixture.Receipts());
        Assert.Empty(Directory.GetFiles(fixture.Root, ".sed-*.tmp"));
    }

    [Fact]
    public async Task CancellationBeforePackageDispatchLeavesNoOutputOrReceipt()
    {
        using var fixture = new Fixture();
        string input = fixture.Room();
        using var tools = fixture.Tools();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = Failure(await tools.CallAsync("sed_package_apply", Args(new {
            level = input, request = fixture.Request(), output = "cancelled.goo",
            expectedOutputSha256 = new string('0', 64), expectedManifestSha256 = new string('1', 64),
            operationId = Guid.NewGuid().ToString()
        }), cancellation.Token), "cancelled");
        Assert.Contains("sed_outcome", failure.GetProperty("message").GetString());
        Assert.False(File.Exists(fixture.PathFor("cancelled.goo")));
        Assert.Empty(fixture.Receipts());
    }

    private static Dictionary<string, JsonElement> CommitArgs(string input, object request, string output, JsonElement plan, string id)
        => Args(new { level = input, request, output, operationId = id,
            expectedOutputSha256 = plan.GetProperty("outputSha256").GetString(),
            expectedManifestSha256 = plan.GetProperty("manifestSha256").GetString() });

    private static JsonElement Envelope(CallToolResult result)
    {
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        using var json = JsonDocument.Parse(text);
        Assert.True(result.StructuredContent.HasValue);
        Assert.True(JsonElement.DeepEquals(json.RootElement, result.StructuredContent.Value));
        return json.RootElement.Clone();
    }

    private static JsonElement Success(CallToolResult result)
    {
        var envelope = Envelope(result);
        Assert.False(result.IsError);
        Assert.True(envelope.GetProperty("ok").GetBoolean(), envelope.GetRawText());
        return envelope.GetProperty("result");
    }

    private static JsonElement Failure(CallToolResult result, string code)
    {
        var envelope = Envelope(result);
        Assert.True(result.IsError);
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        var error = envelope.GetProperty("error");
        Assert.Equal(code, error.GetProperty("code").GetString());
        return error;
    }

    private static Dictionary<string, JsonElement> Args(object value)
    {
        var element = JsonSerializer.SerializeToElement(value, AgentWorkspace.JsonOptions);
        return element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "sed-mcp-package-" + Guid.NewGuid().ToString("N"));
        public HostOptions Options => new(Root, PathFor("missing-fixed-agent"), "original-package-test", []);
        public Fixture() => Directory.CreateDirectory(Root);
        public string PathFor(string name) => Path.Combine(Root, name);
        public SedTools Tools() => new(Options);
        public string[] Receipts() => Directory.Exists(PathFor(".sed-mcp/receipts"))
            ? Directory.GetFiles(PathFor(".sed-mcp/receipts"), "*.json") : [];
        public string Room(ProjectType game = ProjectType.MysteriesOfTheSith)
        {
            string path = PathFor("room.jkl");
            File.WriteAllText(path, JklWriter.Build(new JklDocument(LevelFactory.CreateUntexturedRoom(game))));
            return path;
        }
        public string Archive(string content)
        {
            string path = PathFor("original.goo");
            GobWriter.Build(path, [("misc/unused.txt", Encoding.ASCII.GetBytes(content))]);
            return path;
        }
        public string LargeReviewArchive()
        {
            const int count = 3000;
            string path = PathFor("large-review.goo");
            var model = new StringBuilder($"SECTION: MODELRESOURCE\nMATERIALS {count}\n");
            var entries = new List<(string name, byte[] data)>();
            for (int index = 0; index < count; index++)
            {
                string name = $"fixture_{index:D4}_" + new string('x', 100) + ".mat";
                model.Append(index).Append(": ").Append(name).Append('\n');
                // Leaf presence is sufficient for this metadata-budget test;
                // these original opaque bytes make no material-validity claim.
                entries.Add(("mat/" + name, new byte[] { 1 }));
            }
            model.Append("END\n");
            entries.Add(("3do/review.3do", Encoding.ASCII.GetBytes(model.ToString())));
            GobWriter.Build(path, entries);
            return path;
        }
        public Dictionary<string, object> Request(string? archive = null) => new() {
            ["schemaVersion"] = 1,
            ["inputSha256"] = AgentWorkspace.Hash(File.ReadAllBytes(PathFor("room.jkl"))),
            ["levelFileName"] = "original.jkl",
            ["resourceArchives"] = archive is null ? Array.Empty<object>() : new object[] { new {
                path = archive, sha256 = AgentWorkspace.Hash(File.ReadAllBytes(archive)) } },
            ["stockArchives"] = Array.Empty<string>()
        };
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

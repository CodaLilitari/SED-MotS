using System.Text.Json;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;
using Sed.Formats.Gob;
using Xunit;

namespace Sed.Core.Tests;

public sealed class AgentWorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sed-agent-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _input;
    public AgentWorkspaceTests()
    {
        Directory.CreateDirectory(_directory);
        _input = Path.Combine(_directory, "original.jkl");
        AgentWorkspace.Create(_input, "mots");
    }

    private string Patch(string operations, string? hash = null) =>
        "{\"schemaVersion\":1,\"inputSha256\":\"" + (hash ?? AgentWorkspace.Load(_input).Hash) + "\",\"operations\":" + operations + "}";

    [Theory]
    [InlineData("jk", Sed.Core.Model.ProjectType.JediKnight)]
    [InlineData("mots", Sed.Core.Model.ProjectType.MysteriesOfTheSith)]
    public void NewRoomRetainsSelectedGame(string game, Sed.Core.Model.ProjectType expected)
    {
        string path = Path.Combine(_directory, game + ".jkl");
        AgentWorkspace.Create(path, game);
        Assert.Equal(expected, AgentWorkspace.Load(path).Document.Level.Kind);
    }

    [Fact]
    public void PlannedEditIsReadOnlyAndCommittedOutputReopens()
    {
        byte[] original = File.ReadAllBytes(_input);
        var plan = AgentWorkspace.Plan(_input, Patch("[{\"op\":\"setGravity\",\"value\":6},{\"op\":\"setSectorAmbient\",\"sector\":0,\"value\":0.6}]"));
        Assert.Equal(original, File.ReadAllBytes(_input));
        Assert.Single(Directory.GetFiles(_directory));
        string output = Path.Combine(_directory, "edited.jkl");
        AgentWorkspace.Commit(plan, output);
        var level = AgentWorkspace.Load(output).Document.Level;
        Assert.Equal(6f, level.Header.Gravity);
        Assert.Equal(0.6f, level.Sectors[0].Ambient.B);
        Assert.Equal(original, File.ReadAllBytes(_input));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.True(JsonSerializer.SerializeToElement(AgentWorkspace.Validate(output), AgentWorkspace.JsonOptions).GetProperty("valid").GetBoolean());
    }

    [Fact]
    public void ReusingAStaleInputHashFailsBeforeAnEdit()
    {
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Plan(_input,
            Patch("[{\"op\":\"setGravity\",\"value\":6}]", new string('0', 64))));
        Assert.Equal(4f, AgentWorkspace.Load(_input).Document.Level.Header.Gravity);
    }

    [Fact]
    public void InputChangedAfterPlanningCannotBeCommitted()
    {
        var plan = AgentWorkspace.Plan(_input, Patch("[{\"op\":\"setGravity\",\"value\":6}]"));
        File.AppendAllText(_input, "\n# external change\n");
        string output = Path.Combine(_directory, "edited.jkl");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ExistingOutputAndOriginalAreNeverOverwritten()
    {
        var plan = AgentWorkspace.Plan(_input, Patch("[{\"op\":\"setGravity\",\"value\":6}]"));
        string output = Path.Combine(_directory, "edited.jkl");
        File.WriteAllText(output, "user work");
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, output));
        Assert.Equal("user work", File.ReadAllText(output));
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, _input));
        Assert.Equal(4f, AgentWorkspace.Load(_input).Document.Level.Header.Gravity);
    }

    [Theory]
    [InlineData("[{\"op\":\"setGravity\",\"value\":6,\"value\":7}]")]
    [InlineData("[{\"op\":\"setGravity\",\"value\":6,\"shell\":\"anything\"}]")]
    [InlineData("[{\"op\":\"setGravity\",\"value\":-1}]")]
    [InlineData("[{\"op\":\"setGravity\",\"value\":1e999}]")]
    [InlineData("[{\"op\":\"setSectorFlags\",\"sector\":-1,\"flags\":0}]")]
    [InlineData("[{\"op\":\"runShell\"}]")]
    [InlineData("[]")]
    public void InvalidOperationsLeaveTheInputUntouched(string operations)
    {
        byte[] before = File.ReadAllBytes(_input);
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(_input, Patch(operations)));
        Assert.Equal(before, File.ReadAllBytes(_input));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void LaterInvalidOperationDoesNotPersistEarlierValidOperation()
    {
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.Plan(_input,
            Patch("[{\"op\":\"setGravity\",\"value\":6},{\"op\":\"setSectorFlags\",\"sector\":999,\"flags\":0}]")));
        Assert.Equal(4f, AgentWorkspace.Load(_input).Document.Level.Header.Gravity);
    }

    [Fact]
    public void LaterOperationsCanAddressAppendedSectors()
    {
        var plan = AgentWorkspace.Plan(_input, Patch("[{\"op\":\"addBox\",\"center\":[5,0,0],\"halfSize\":1},{\"op\":\"setSectorAmbient\",\"sector\":1,\"value\":0.3}]"));
        var level = JklParser.ParseDocument(plan.OutputText).Level;
        Assert.Equal(2, level.Sectors.Count);
        Assert.Equal(0.3f, level.Sectors[1].Ambient.B);
    }

    [Fact]
    public void MalformedGeometryFailsValidationWithoutOutput()
    {
        var doc = AgentWorkspace.Load(_input).Document;
        var p = doc.Level.Sectors[0].Vertices[1].Position;
        string position = JsonSerializer.Serialize(new[] { p.X, p.Y, p.Z });
        Assert.Throws<AgentValidationException>(() => AgentWorkspace.Plan(_input,
            Patch("[{\"op\":\"setVertexPosition\",\"sector\":0,\"vertex\":0,\"position\":" + position + "}]")));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ArchiveRecoveryRequiresExactEntryAndPreservesOriginalBytes()
    {
        string archive = Path.Combine(_directory, "original.goo");
        byte[] original = [0x23, 0x20, 0xe9, 0x0d, 0x0a];
        GobWriter.Build(archive, [("jkl/one.jkl", original), ("cog/logic.cog", original)]);
        string output = Path.Combine(_directory, "recovered.jkl");
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.ExtractLevel(archive, "one.jkl", output));
        Assert.Throws<AgentRequestException>(() => AgentWorkspace.ExtractLevel(archive, "cog/logic.cog", output));
        AgentWorkspace.ExtractLevel(archive, "jkl/one.jkl", output);
        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.Throws<AgentConflictException>(() => AgentWorkspace.ExtractLevel(archive, "jkl/one.jkl", output));
        var inventory = JsonSerializer.SerializeToElement(AgentWorkspace.InspectArchive(archive), AgentWorkspace.JsonOptions);
        Assert.Single(inventory.GetProperty("levels").EnumerateArray());
    }

    [Fact]
    public void LooseLegacyTextRetainsNonUtf8CommentsThroughAnUnrelatedEdit()
    {
        string original = File.ReadAllText(_input) + "\n# caf\u00e9\n";
        File.WriteAllBytes(_input, System.Text.Encoding.Latin1.GetBytes(original));
        var edit = AgentWorkspace.Plan(_input, Patch("[{\"op\":\"setGravity\",\"value\":6}]"));
        Assert.Contains("# caf\u00e9", edit.OutputText);
        Assert.DoesNotContain("\ufffd", edit.OutputText);
        string output = Path.Combine(_directory, "unicode.jkl"); AgentWorkspace.Commit(edit, output);
        Assert.Contains("# caf\u00e9", string.Join('\n', AgentWorkspace.Load(output).Document.SourceLines));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

using System.Text;
using System.Text.Json;
using Sed.Formats.Automation;
using Xunit;

namespace Sed.Core.Tests;

public sealed class AgentTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestTransportPreservesUnicodeAndOptionalUtf8Bom(bool bom)
    {
        const string json = "{\"path\":\"C:/projects/caf\u00e9/level.jkl\"}";
        byte[] encoded = Encoding.UTF8.GetBytes(json);
        using var input = new MemoryStream(bom ? new byte[] { 0xef, 0xbb, 0xbf }.Concat(encoded).ToArray() : encoded);
        Assert.Equal(json, AgentRequestReader.Read(input));
        Assert.True(input.CanRead);
    }

    [Fact]
    public void RequestTransportRejectsInvalidUtf8()
    {
        using var input = new MemoryStream([0xc3, 0x28]);
        Assert.Throws<AgentRequestException>(() => AgentRequestReader.Read(input));
    }

    [Fact]
    public void RequestTransportBoundsBytesBeforeDecodingOrReadingTheWholeStream()
    {
        using var input = new MemoryStream(Enumerable.Repeat((byte)' ', AgentWorkspace.MaxRequestBytes + 8000).ToArray());
        Assert.Throws<AgentRequestException>(() => AgentRequestReader.Read(input));
        Assert.Equal(AgentWorkspace.MaxRequestBytes + 1, input.Position);
    }

    [Fact]
    public void RequestTransportAcceptsExactLimit()
    {
        using var input = new MemoryStream(Enumerable.Repeat((byte)' ', AgentWorkspace.MaxRequestBytes).ToArray());
        Assert.Equal(AgentWorkspace.MaxRequestBytes, AgentRequestReader.Read(input).Length);
    }

    [Fact]
    public void ReviewedPlanGuardRejectsDifferentOutputBeforeWriting()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sed-agent-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.jkl"), output = Path.Combine(directory, "output.jkl");
            AgentWorkspace.Create(source, "mots");
            string patch = JsonSerializer.Serialize(new { schemaVersion = 1, inputSha256 = AgentWorkspace.Load(source).Hash,
                operations = new[] { new { op = "setGravity", value = 6 } } });
            var plan = AgentWorkspace.Plan(source, patch);
            string hash = JsonSerializer.SerializeToElement(plan.Report, AgentWorkspace.JsonOptions).GetProperty("outputSha256").GetString()!;
            Assert.Throws<AgentRequestException>(() => AgentWorkspace.Commit(plan, output, "invalid"));
            Assert.Throws<AgentConflictException>(() => AgentWorkspace.Commit(plan, output, new string('0', 64)));
            Assert.False(File.Exists(output));
            AgentWorkspace.Commit(plan, output, hash.ToUpperInvariant());
            Assert.Equal(hash, AgentWorkspace.Load(output).Hash);
        }
        finally { Directory.Delete(directory, true); }
    }
}

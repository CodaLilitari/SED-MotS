using Sed.Mcp;

namespace Sed.Mcp.Tests;

public sealed class HostOptionsTests
{
    [Fact]
    public void DefaultSnapshotWorkerUsesNativeSiblingApphost()
    {
        var options = HostOptions.Parse(["--workspace", Path.GetTempPath()]);
        string expectedName = OperatingSystem.IsWindows() ? "sed-agent.exe" : "sed-agent";
        Assert.Equal(expectedName, Path.GetFileName(options.AgentPath));
        Assert.Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "agent")),
            Path.GetDirectoryName(options.AgentPath));
        Assert.Equal(HostOptions.Version, options.AgentVersion);
    }

    [Fact]
    public void ExplicitWorkerPathAndVersionRemainExact()
    {
        string agent = Path.Combine(Path.GetTempPath(), "custom worker", "chosen-agent");
        var options = HostOptions.Parse(["--workspace", Path.GetTempPath(), "--agent", agent,
            "--agent-version", "reviewed-version"]);
        Assert.Equal(Path.GetFullPath(agent), options.AgentPath);
        Assert.Equal("reviewed-version", options.AgentVersion);
    }
}

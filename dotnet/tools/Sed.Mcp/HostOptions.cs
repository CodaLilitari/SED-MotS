using System.Reflection;
using System.Text.Json;

namespace Sed.Mcp;

public sealed record HostOptions(string Workspace, string AgentPath, string AgentVersion, string[] ResourceRoots)
{
    public static string Version => typeof(HostOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static HostOptions Parse(string[] args)
    {
        string? workspace = null, agent = null, version = null;
        List<string> roots = [];
        for (int index = 0; index < args.Length; index++)
        {
            string option = args[index];
            if (++index >= args.Length) throw new ArgumentException($"Missing value for {option}.");
            string value = args[index];
            switch (option)
            {
                case "--workspace" when workspace is null: workspace = value; break;
                case "--agent" when agent is null: agent = value; break;
                case "--agent-version" when version is null: version = value; break;
                case "--resource-root": roots.Add(value); break;
                default: throw new ArgumentException($"Unknown or repeated option {option}.");
            }
        }
        if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("--workspace must identify an existing level-project directory.");
        string workerName = OperatingSystem.IsWindows() ? "sed-agent.exe" : "sed-agent";
        return new(workspace, Path.GetFullPath(agent ?? Path.Combine(AppContext.BaseDirectory, "..", "agent", workerName)),
            version ?? Version, roots.ToArray());
    }
}

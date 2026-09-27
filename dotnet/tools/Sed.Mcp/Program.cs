using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sed.Mcp;

try
{
    if (args is ["--version"])
    { Console.WriteLine(JsonSerializer.Serialize(new { name = "sed-mcp", version = HostOptions.Version, sdk = "ModelContextProtocol.Core/1.4.1" })); return 0; }
    if (args is ["--help"] or [])
    { Console.WriteLine("sed-mcp --workspace <existing-project> [--resource-root <read-only-folder>] [--agent <sed-agent.exe>] [--agent-version <exact-version>]\nLocal stdio MCP. No HTTP listener or model service. Packaged sibling agent is the default snapshot worker."); return 0; }
    var options = HostOptions.Parse(args);
    using var service = new SedTools(options);
    var serverOptions = new McpServerOptions
    {
        ServerInfo = new() { Name = "sed-mcp", Version = HostOptions.Version },
        ServerInstructions = ToolCatalog.Instructions,
        Capabilities = new() { Tools = new() { ListChanged = false } },
        Handlers = new()
        {
            ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = ToolCatalog.Tools.ToList() }),
            CallToolHandler = async (context, token) => await service.CallAsync(context.Params?.Name ?? "", context.Params?.Arguments, token)
        }
    };
    await using var server = McpServer.Create(new StreamServerTransport(
        new BoundedInputStream(Console.OpenStandardInput()), Console.OpenStandardOutput(), "sed-mcp"), serverOptions);
    await server.RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"sed-mcp: {error.Message}");
    return 1;
}

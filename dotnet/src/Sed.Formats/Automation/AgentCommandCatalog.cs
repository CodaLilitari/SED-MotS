namespace Sed.Formats.Automation;

/// <summary>Named argument schemas describe CLI positions; they are not a second execution protocol.</summary>
public static class AgentCommandCatalog
{
    public sealed record Command(string Name, string Usage, string Description, bool WritesFiles,
        string[] PositionalArguments, object ArgumentsJsonSchema);

    public static IReadOnlyList<Command> Commands()
    {
        object Text(string description) => new { type = "string", minLength = 1, description };
        object Choice(params string[] choices) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = choices };
        object path = Text("Local path; pass as one process argument. Native text projects may use .jed, .sed or .ned."),
            output = Text("New .jkl path in an existing directory. Existing files are never overwritten."),
            request = Text("UTF-8 JSON request file, or '-' to read one bounded JSON document from stdin until EOF.");
        Command Make(string name, string usage, string description, bool writes, string[] required,
            params (string Name, object Schema)[] fields) => new(name, usage, description, writes, fields.Select(f => f.Name).ToArray(),
                new Dictionary<string, object> { ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = required,
                    ["properties"] = fields.ToDictionary(f => f.Name, f => f.Schema) });
        return [
            Make("help", "help [command]", "Compact command discovery. --help and -h are aliases; no arguments also shows help.", false, [],
                ("command", Text("Optional command name."))),
            Make("version", "--version", "Executable build version and supported protocol capabilities.", false, []),
            Make("schema", "schema [patch|query|commands|launch-plan|operation]", "Machine-readable schemas; select one operation to reduce context size.", false, [],
                ("selection", Text("patch, query, commands, launch-plan, or a patch operation name."))),
            Make("inspect", "inspect <level> [sector-index]", "Legacy full inventory with optional sector geometry. Prefer bounded query for large levels.", false, ["level"],
                ("level", path), ("sectorIndex", new { type = "integer", minimum = 0, maximum = int.MaxValue })),
            Make("query", "query <level> <query.json|->", "Bounded readback of header, sectors, things, lights, templates, materials, surfaces or vertices.", false, ["level", "request"],
                ("level", path), ("request", request)),
            Make("validate", "validate <level>", "Structural diagnostics with object and parameter selectors; no gameplay or resource-load claim.", false, ["level"], ("level", path)),
            Make("plan", "plan <level> <patch.json|->", "Apply a patch to an isolated model and return a reviewable output hash. No writes.", false, ["level", "request"],
                ("level", path), ("request", request)),
            Make("apply", "apply <level> <patch.json|-> <new-output.jkl> [expected-output-sha256]", "Replan, optionally require the reviewed output hash, recheck inputs, and create a new file atomically.", true, ["level", "request", "output"],
                ("level", path), ("request", request), ("output", output), ("expectedOutputSha256", new { type = "string", pattern = "^[0-9a-fA-F]{64}$" })),
            Make("new", "new <new-output.jkl> [mots|jk]", "Create an untextured room scaffold; defaults to MotS.", true, ["output"], ("output", output), ("game", Choice("mots", "jk"))),
            Make("convert", "convert <legacy.jed> <new-output.jkl>", "Convert supported native text project data to a validated JKL and report preservation warnings.", true, ["level", "output"], ("level", path), ("output", output)),
            Make("archive", "archive <archive.gob|archive.goo>", "List exact JKL entry names and the source archive hash.", false, ["archive"], ("archive", path)),
            Make("extract", "extract <archive> <exact-entry-name> <new-output.jkl>", "Recover one exact JKL archive entry byte-for-byte into a new file.", true, ["archive", "entry", "output"],
                ("archive", path), ("entry", Text("Exact entry name returned by archive; no wildcard or basename search.")), ("output", output)),
            Make("launch-plan", "launch-plan <launch-request.json|->", "Check local paths and return game invocation arguments without launching anything.", false, ["request"], ("request", request)),
        ];
    }

    public static object LaunchRequestSchema() => new Dictionary<string, object>
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["type"] = "object", ["additionalProperties"] = false,
        ["required"] = new[] { "game", "gameDirectory", "projectDirectory", "levelFileName" },
        ["properties"] = new Dictionary<string, object>
        {
            ["game"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "JediKnight", "MysteriesOfTheSith" } },
            ["gameDirectory"] = new { type = "string", minLength = 1 }, ["projectDirectory"] = new { type = "string", minLength = 1 },
            ["levelFileName"] = new { type = "string", minLength = 1 }, ["executable"] = new { type = new[] { "string", "null" } },
            ["engine"] = new Dictionary<string, object> { ["type"] = "string", ["enum"] = new[] { "Auto", "OpenJKDF2", "Retail" }, ["default"] = "Auto" },
        },
    };
}

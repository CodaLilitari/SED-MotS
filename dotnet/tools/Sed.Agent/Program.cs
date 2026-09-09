using System.Text.Json;
using System.Text.Json.Serialization;
using Sed.Formats.Automation;
using Sed.Formats.Launching;

// stdout is exclusively a JSON result; stderr is reserved for unexpected host
// failures. No GUI, graphics device, engine, shell, or network service is started.
try
{
    object result;
    int exitCode = 0;
    switch (args.FirstOrDefault())
    {
        case "schema" when args.Length == 1:
            result = new { schemaVersion = 1, commands = new[] {
                "inspect <level.jkl> [sector-index]", "validate <level.jkl>",
                "plan <level.jkl> <patch.json>", "apply <level.jkl> <patch.json> <new-output.jkl>",
                "new <new-output.jkl> [mots|jk]", "launch-plan <launch-request.json>",
                "convert <legacy.jed> <new-output.jkl>", "archive <archive.gob|archive.goo>",
                "extract <archive.gob|archive.goo> <exact-entry-name> <new-output.jkl>" },
                patch = new { schemaVersion = 1, inputSha256 = "SHA256 returned by inspect", operations = AgentWorkspace.OperationFields },
                patchJsonSchema = AgentSchema.Patch(),
                selectors = "Zero-based indices from inspect, bound to the exact inputSha256.",
                vectors = "position, orientation, center, rgb are arrays of three finite numbers; orientation is pitch/yaw/roll in degrees.",
                limits = new { operations = AgentWorkspace.MaxOperations, requestBytes = AgentWorkspace.MaxRequestBytes, levelBytes = AgentWorkspace.MaxLevelBytes },
                output = "plan is read-only; apply creates a new output and never overwrites an existing file.",
                exitCodes = new { success = 0, invalidRequest = 2, validationFailed = 3, conflict = 4, ioError = 5 } };
            break;
        case "inspect" when args.Length is 2 or 3:
            result = AgentWorkspace.Inspect(args[1], args.Length == 3 ? int.Parse(args[2]) : null);
            break;
        case "validate" when args.Length == 2:
            result = AgentWorkspace.Validate(args[1]);
            var validation = JsonSerializer.SerializeToElement(result, AgentWorkspace.JsonOptions);
            exitCode = validation.GetProperty("valid").GetBoolean() ? 0 : 3;
            break;
        case "plan" when args.Length == 3:
            result = AgentWorkspace.Plan(args[1], ReadRequest(args[2])).Report;
            break;
        case "apply" when args.Length == 4:
            result = AgentWorkspace.Commit(AgentWorkspace.Plan(args[1], ReadRequest(args[2])), args[3]);
            break;
        case "new" when args.Length is 2 or 3:
            result = AgentWorkspace.Create(args[1], args.Length == 3 ? args[2] : "mots");
            break;
        case "convert" when args.Length == 3:
            result = AgentWorkspace.Commit(AgentWorkspace.ConvertDocument(args[1]), args[2]);
            break;
        case "archive" when args.Length == 2:
            result = AgentWorkspace.InspectArchive(args[1]);
            break;
        case "extract" when args.Length == 4:
            result = AgentWorkspace.ExtractLevel(args[1], args[2], args[3]);
            break;
        case "launch-plan" when args.Length == 2:
            var launchOptions = new JsonSerializerOptions(AgentWorkspace.JsonOptions)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
            launchOptions.Converters.Add(new JsonStringEnumConverter());
            var launch = JsonSerializer.Deserialize<LaunchRequest>(ReadRequest(args[1]), launchOptions)
                ?? throw new AgentRequestException("Expected a launch request object.");
            result = new { schemaVersion = 1, plan = GameLauncher.PrepareLaunch(launch),
                note = "Read-only plan. No files were written and no executable was started." };
            break;
        default: throw new AgentRequestException("Use sed-agent schema for commands and the versioned patch contract.");
    }
    Console.WriteLine(JsonSerializer.Serialize(result, AgentWorkspace.JsonOptions));
    return exitCode;
}
catch (Exception error) when (error is AgentRequestException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException or NotSupportedException)
{ return Failure(2, "invalid_request", error.Message); }
catch (AgentValidationException error)
{ return Failure(3, "validation_failed", error.Message, error.Issues); }
catch (AgentConflictException error)
{ return Failure(4, "conflict", error.Message); }
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{ return Failure(5, "io_error", error.Message); }

static string ReadRequest(string path)
{
    if (new FileInfo(path).Length > AgentWorkspace.MaxRequestBytes) throw new AgentRequestException("Patch exceeds 1 MiB.");
    return File.ReadAllText(path);
}
static int Failure(int code, string type, string message, object? issues = null)
{
    Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, error = type, message, issues }, AgentWorkspace.JsonOptions));
    return code;
}

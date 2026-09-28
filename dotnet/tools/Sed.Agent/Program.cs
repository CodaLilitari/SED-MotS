using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Sed.Formats.Automation;
using Sed.Formats.Launching;

// stdout is exclusively a JSON result; stderr is reserved for unexpected host
// failures. Only snapshot creates an offscreen graphics device. No GUI, game,
// shell or network service is started.
try
{
    object result;
    int exitCode = 0;
    switch (args.FirstOrDefault())
    {
        case null:
        case "help" when args.Length is 1 or 2:
        case "--help" when args.Length == 1:
        case "-h" when args.Length == 1:
            var commands = AgentCommandCatalog.Commands();
            if (args.Length == 2)
            {
                commands = commands.Where(c => c.Name == args[1]).ToArray();
                if (commands.Count == 0) throw new AgentRequestException("Unknown command. Use help to list commands.");
            }
            result = new { schemaVersion = 1, commands = commands.Select(c => new { c.Name, c.Usage, c.Description, c.WritesFiles }),
                next = "Use schema query for bounded readback, schema select for spatial targets, schema <operation> for a focused patch schema, and '-' to supply JSON on stdin." };
            break;
        case "--version" when args.Length == 1:
        case "version" when args.Length == 1:
            result = new { schemaVersion = 1, name = "sed-agent",
                version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                capabilities = Capabilities() };
            break;
        case "schema" when args.Length == 2:
            result = args[1] switch {
                "query" => AgentQueries.Schema(), "select" => AgentSelections.Schema(),
                "commands" => new { schemaVersion = 1, commands = AgentCommandCatalog.Commands() },
                "launch-plan" => AgentCommandCatalog.LaunchRequestSchema(), "patch" => AgentSchema.Patch(),
                "snapshot" => AgentSnapshotRequest.Schema(),
                "resources" => AgentWorkspace.ResourceRequestSchema(),
                "bindings" => AgentWorkspace.BindingsRequestSchema(),
                "lighting" => AgentWorkspace.LightingRequestSchema(),
                "assets" => AgentWorkspace.AssetsRequestSchema(),
                _ => AgentSchema.Patch(args[1]) };
            break;
        case "schema" when args.Length == 1:
            result = new { schemaVersion = 1, commands = AgentCommandCatalog.Commands().Select(c => c.Usage),
                patch = new { schemaVersion = 1, inputSha256 = "SHA256 returned by inspect", operations = AgentWorkspace.OperationFields },
                patchJsonSchema = AgentSchema.Patch(),
                queryJsonSchema = AgentQueries.Schema(),
                selectJsonSchema = AgentSelections.Schema(),
                commandSchemas = AgentCommandCatalog.Commands(),
                capabilities = Capabilities(),
                selectors = "Zero-based indices remain supported. select returns unique spatial target tokens bound to sourceRevision and containing-sector geometry; supported operations accept target/otherTarget instead of numeric geometry addresses.",
                vectors = "position, orientation, center, rgb are arrays of three finite numbers; orientation is pitch/yaw/roll in degrees.",
                limits = new { operations = AgentWorkspace.MaxOperations, requestBytes = AgentWorkspace.MaxRequestBytes, levelBytes = AgentWorkspace.MaxLevelBytes },
                output = "plan is read-only; apply creates a new output and never overwrites an existing file.",
                exitCodes = new { success = 0, invalidRequest = 2, validationFailed = 3, conflict = 4, ioError = 5 } };
            break;
        case "inspect" when args.Length is 2 or 3:
            result = AgentWorkspace.Inspect(args[1], args.Length == 3 ? int.Parse(args[2]) : null);
            break;
        case "query" when args.Length == 3:
            result = AgentWorkspace.Query(args[1], ReadRequest(args[2]));
            break;
        case "bindings" when args.Length == 3:
            result = AgentWorkspace.Bindings(args[1], ReadRequest(args[2]));
            break;
        case "lighting" when args.Length == 3:
            result = AgentWorkspace.DiagnoseLighting(args[1], ReadRequest(args[2]));
            break;
        case "assets" when args.Length == 3:
            result = AgentWorkspace.DiagnoseAssets(args[1], ReadRequest(args[2]));
            break;
        case "select" when args.Length == 3:
            result = AgentWorkspace.Select(args[1], ReadRequest(args[2]));
            break;
        case "snapshot" when args.Length == 4:
            result = Sed.Agent.AgentSnapshotRunner.Run(args[1], ReadRequest(args[2]), args[3]);
            break;
        case "validate" when args.Length is 2 or 3:
            result = AgentWorkspace.Validate(args[1], args.Length == 3 ? ReadRequest(args[2]) : null);
            var validation = JsonSerializer.SerializeToElement(result, AgentWorkspace.JsonOptions);
            exitCode = validation.GetProperty("valid").GetBoolean() ? 0 : 3;
            break;
        case "plan" when args.Length == 3:
            result = AgentWorkspace.Plan(args[1], ReadRequest(args[2])).Report;
            break;
        case "apply" when args.Length is 4 or 5:
            result = AgentWorkspace.Commit(AgentWorkspace.Plan(args[1], ReadRequest(args[2])), args[3], args.Length == 5 ? args[4] : null);
            break;
        case "new" when args.Length is 2 or 3:
            result = AgentWorkspace.Create(args[1], args.Length == 3 ? args[2] : "mots");
            break;
        case "convert" when args.Length is 3 or 4 or 5:
            result = AgentWorkspace.Commit(AgentWorkspace.ConvertDocument(args[1], args.Length >= 4 ? ReadRequest(args[3]) : null), args[2], args.Length == 5 ? args[4] : null);
            break;
        case "archive" when args.Length == 2:
            result = AgentWorkspace.InspectArchive(args[1]);
            break;
        case "extract" when args.Length == 4:
            result = AgentWorkspace.ExtractLevel(args[1], args[2], args[3]);
            break;
        case "launch-plan" when args.Length == 2:
            var launchOptions = new JsonSerializerOptions(AgentWorkspace.JsonOptions)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true, MaxDepth = 16,
                AllowDuplicateProperties = false };
            launchOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
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
catch (AgentRequestException error)
{ return Failure(2, "invalid_request", error.Message, details: new { error.OperationIndex, error.Operation, error.JsonPointer }); }
catch (JsonException error)
{ return Failure(2, "invalid_request", error.Message, details: new { jsonPath = error.Path, lineNumber = error.LineNumber, bytePositionInLine = error.BytePositionInLine }); }
catch (Exception error) when (error is FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException or NotSupportedException)
{ return Failure(2, "invalid_request", error.Message); }
catch (AgentValidationException error)
{ return Failure(3, "validation_failed", error.Message, error.Issues); }
catch (AgentConflictException error)
{ return Failure(4, "conflict", error.Message, details: new { error.OperationIndex, error.Operation, error.JsonPointer }); }
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{ return Failure(5, "io_error", error.Message); }

static string ReadRequest(string path)
{
    if (path == "-") return AgentRequestReader.Read(Console.OpenStandardInput());
    using var input = File.OpenRead(path);
    return AgentRequestReader.Read(input);
}
static string[] Capabilities() => ["bounded-query", "stdin-json", "focused-schema", "native-source-revision", "reviewed-output-hash", "operation-diagnostics",
    "neighbor-texture-alignment", "thing-point-containment", "typed-cog-bindings", "guarded-cog-rebinding",
    "typed-cog-creation", "ordered-thing-frame-authoring",
    "world-bake-diagnostics", "direct-asset-diagnostics",
    "resource-registration", "surface-uv", "connected-geometry", "spatial-readback", "spatial-selection", "safe-selector-targets", "offscreen-diagnostic-snapshot", "offscreen-lit-snapshot", "mots-rgb-architecture-preview",
    "all-lod-architecture-validation", "architecture-resource-revision", "resource-validated-export",
    "architecture-authoring", "architecture-source-point-bake", "architecture-world-occluded-bake", "architecture-provenance-readback"];
static int Failure(int code, string type, string message, object? issues = null, object? details = null)
{
    Console.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, error = type, message, issues, details }, AgentWorkspace.JsonOptions));
    return code;
}

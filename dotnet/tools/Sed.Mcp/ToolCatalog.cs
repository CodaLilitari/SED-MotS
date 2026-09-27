using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Sed.Mcp;

public static class ToolCatalog
{
    public const string Instructions = "SED edits isolated level files; it does not control an unsaved GUI document. Discover focused schemas, query/select, plan, then apply to a NEW output using that plan's output hash and resourceRevision. Use one operationId per intended output; after interruption call sed_outcome, never blindly replay with a new ID/path. Snapshots are static previews, not gameplay or retail proof. All paths use configured workspace/read roots.";
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static readonly JsonElement OutputSchema = Element(new { type = "object", required = new[] { "schemaVersion", "ok" },
        properties = new { schemaVersion = new { type = "integer" }, ok = new { type = "boolean" }, result = new { type = "object" }, error = new { type = "object" } } });

    public static IReadOnlyList<Tool> Tools { get; } = Build();
    private static IReadOnlyList<Tool> Build()
    {
        object Text(string description) => new { type = "string", minLength = 1, maxLength = 8192, description };
        object Request(string description) => new { type = "object", description };
        object Choice(params string[] values) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = values };
        object level = Text("Level path in configured read roots; relative paths use workspace. JKL or native JED/SED/NED."),
            output = Text("New output path under workspace; never overwrites an existing file."),
            id = new { type = "string", description = "Fresh GUID for one intended write; preserve it for outcome/replay checks.", minLength = 32, maxLength = 36 },
            hash = new { type = "string", pattern = "^[0-9a-fA-F]{64}$", description = "outputSha256 from the reviewed plan." };
        Tool Make(string name, string description, bool readOnly, string[] required, params (string Name, object Schema)[] fields) => new()
        {
            Name = name, Description = description,
            InputSchema = Element(new Dictionary<string, object> { ["type"] = "object", ["additionalProperties"] = false,
                ["required"] = required, ["properties"] = fields.ToDictionary(x => x.Name, x => x.Schema) }),
            OutputSchema = OutputSchema,
            Annotations = new() { ReadOnlyHint = readOnly, DestructiveHint = false, OpenWorldHint = false, IdempotentHint = readOnly }
        };
        return [
            Make("sed_discover", "Compact capabilities or one exact domain schema. Start here; topic may be query/select/patch/snapshot/resources/bindings/lighting/assets or a patch operation.", true, [], ("topic", Text("Optional focused schema; omitted returns compact capabilities."))),
            Make("sed_query", "Bounded source-identified level readback. Use collection summary first, then focused pages. Does not edit files.", true, ["level","request"], ("level",level),("request",Request("Exact query JSON; discover query schema."))),
            Make("sed_select", "Find a sector/face by geometry, returning source-bound selectors or explicit ambiguity.", true, ["level","request"],("level",level),("request",Request("Exact select JSON."))),
            Make("sed_diagnose", "Validate a level or inspect assets, world bake contribution, or typed COG bindings. Explicit resources remain hash-bound.", true,["level","kind"],("level",level),("kind",Choice("validate","assets","lighting","bindings")),("request",Request("Domain request; required except resource-free validate."))),
            Make("sed_plan", "Calculate isolated edits and exact output hash without creating output. Review warnings, effects and plan resourceRevision.",true,["level","request"],("level",level),("request",Request("Exact patch JSON; discover operation schemas."))),
            Make("sed_apply", "Recompute and commit a reviewed plan to a new file. Required output hash and operation ID; after interruption use sed_outcome.",false,["level","request","output","expectedOutputSha256","operationId"],("level",level),("request",Request("Reviewed patch including its plan resourceRevision when resources were read.")),("output",output),("expectedOutputSha256",hash),("operationId",id)),
            Make("sed_new", "Plan or create an original room scaffold. Plan omits output/operationId; commit requires output, operationId and hash returned by plan.",false,["game"],("game",Choice("jk","mots")),("output",output),("expectedOutputSha256",hash),("operationId",id)),
            Make("sed_convert", "Plan or save an imported level as extended JKL. Omit commit arguments to inspect. Commit requires reviewed output hash plus expectedInputSha256 and expectedSourceRevision from that same plan.",false,["level"],("level",level),("request",Request("Optional resource validation request.")),("output",output),("expectedOutputSha256",hash),("operationId",id),
                ("expectedInputSha256", new { type = "string", pattern = "^[0-9a-fA-F]{64}$", description = "Required for commit: inputSha256 from the reviewed conversion plan." }),
                ("expectedSourceRevision", new { type = "string", pattern = "^[0-9a-fA-F]{64}$", description = "Required for commit: sourceRevision from the reviewed conversion plan, including native template dependencies." })),
            Make("sed_archive", "Inspect archive directory metadata with a bounded response; this does not execute or extract content.",true,["archive"],("archive",Text("Explicit .gob/.goo archive path in configured read roots."))),
            Make("sed_snapshot", "Render static diagnostic/lit PNG from JKL using the fixed isolated worker. Convert native projects first. Returns inline image and source/camera/resource receipt. Writes only a new PNG; not engine/gameplay proof.",false,["level","request","output"],("level",level),("request",Request("Exact snapshot JSON with explicit camera and source identity.")),("output",output)),
            Make("sed_outcome", "Read durable edit state and current output evidence after completion, cancellation or reconnect. Matching content does not prove this call created it. Never resumes work.",true,["operationId"],("operationId",id))
        ];
    }
    public static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Json);
}

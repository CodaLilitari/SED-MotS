namespace Sed.Formats.Automation;

/// <summary>JSON Schema for constructing the exact version-one patch protocol.</summary>
public static class AgentSchema
{
    public static object Patch()
    {
        object Number(double minimum, double maximum) => new { type = "number", minimum, maximum };
        object Index() => new { type = "integer", minimum = 0 };
        object Vector(double min, double max) => new { type = "array", minItems = 3, maxItems = 3, items = Number(min, max) };
        object Token() => new { type = "string", minLength = 1, maxLength = 127, pattern = "^[!$-~]+$" };
        var variants = new List<object>();
        foreach (var (operation, fields) in AgentWorkspace.OperationFields)
        {
            var properties = new Dictionary<string, object> { ["op"] = new Dictionary<string, object> { ["const"] = operation } };
            foreach (var field in fields)
                properties[field] = field switch
                {
                    "thing" or "sector" or "surface" or "vertex" or "materialIndex" => Index(),
                    "position" or "center" or "orientation" => Vector(-1_000_000, 1_000_000),
                    "rgb" => Vector(0, 1),
                    "flags" => new { type = "integer", minimum = 0, maximum = (long)uint.MaxValue },
                    "halfSize" or "range" => Number(0.0001, 100000),
                    "intensity" => Number(0, 1000),
                    "value" when operation == "setGravity" => Number(0, 10000),
                    "value" when operation == "setSectorAmbient" => Number(0, 1),
                    "value" when operation == "setVertexLight" => Number(0, 1000),
                    "channel" => new Dictionary<string, object> { ["enum"] = new[] { "mono", "red", "green", "blue" } },
                    "key" => new { type = "string", minLength = 1, maxLength = 127, pattern = "^[A-Za-z0-9_]+$" },
                    _ => Token(),
                };
            variants.Add(new { type = "object", additionalProperties = false,
                required = new[] { "op" }.Concat(fields).ToArray(), properties });
        }
        return new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "SED MotS agent patch version 1",
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new[] { "schemaVersion", "inputSha256", "operations" },
            ["properties"] = new Dictionary<string, object>
            {
                ["schemaVersion"] = new Dictionary<string, object> { ["const"] = 1 },
                ["inputSha256"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$" },
                ["operations"] = new { type = "array", minItems = 1, maxItems = AgentWorkspace.MaxOperations,
                    items = new { oneOf = variants } },
            },
        };
    }
}

using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

/// <summary>JSON Schema for constructing the exact version-one patch protocol.</summary>
public static class AgentSchema
{
    public static object Patch(string? operationName = null)
    {
        if (operationName is not null && !AgentWorkspace.OperationFields.ContainsKey(operationName))
            throw new AgentRequestException($"Unknown schema '{operationName}'. Use schema commands or schema for discovery.");
        object Number(double minimum, double maximum) => new { type = "number", minimum, maximum };
        object Index() => new { type = "integer", minimum = 0, maximum = int.MaxValue };
        object Vector(double min, double max) => new { type = "array", minItems = 3, maxItems = 3, items = Number(min, max) };
        object Token() => new { type = "string", minLength = 1, maxLength = 127, pattern = "^[!$-~]+$" };
        var variants = new List<object>();
        foreach (var (operation, fields) in AgentWorkspace.OperationFields)
        {
            if (operationName is not null && operation != operationName) continue;
            var properties = new Dictionary<string, object> { ["op"] = new Dictionary<string, object> { ["const"] = operation } };
            foreach (var field in fields)
                properties[field] = field switch
                {
                    "thing" or "sector" or "surface" or "vertex" or "materialIndex" or "colormapIndex" or "otherSector" or "otherSurface" => Index(),
                    "position" or "center" or "orientation" => Vector(-1_000_000, 1_000_000),
                    "rgb" => Vector(0, 1),
                    "flags" => new { type = "integer", minimum = 0, maximum = (long)uint.MaxValue },
                    "halfSize" or "range" or "distance" => Number(0.0001, 100000),
                    "width" or "height" => new { type = "integer", minimum = 1, maximum = 16384 },
                    "geometryMode" => new { type = "integer", minimum = 0, maximum = 4 },
                    "lightMode" or "textureMode" => new { type = "integer", minimum = 0, maximum = 3 },
                    "uv" => new { type = "array", minItems = 3, maxItems = 64, items = new { type = "array", minItems = 2, maxItems = 2, items = Number(-1_000_000, 1_000_000) } },
                    "intensity" => Number(0, 1000),
                    "value" when operation == "setGravity" => Number(0, 10000),
                    "value" when operation == "setSectorAmbient" => Number(0, 1),
                    "value" when operation == "setVertexLight" => Number(0, 1000),
                    "channel" => new Dictionary<string, object> { ["enum"] = new[] { "mono", "red", "green", "blue" } },
                    "key" => new { type = "string", minLength = 1, maxLength = 127, pattern = "^[A-Za-z0-9_]+$" },
                    _ => Token(),
                };
            variants.Add(new { type = "object", description = OperationDescription(operation), additionalProperties = false,
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
                ["sourceRevision"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$",
                    description = "Required for native JED/SED/NED inputs; copy from inspect or query. Binds template dependencies as well as source bytes." },
                ["resourceArchives"] = ResourceManifest.Schema(),
                ["resourceRevision"] = new { type = "string", pattern = "^[0-9a-fA-F]{64}$",
                    description = "Requires resourceArchives. Copy the reviewed plan's resourceRevision to bind current model validation separately from document identity." },
                ["operations"] = new { type = "array", minItems = 1, maxItems = AgentWorkspace.MaxOperations,
                    items = new { oneOf = variants } },
            },
        };
    }

    private static string OperationDescription(string operation) => operation switch
    {
        "setThingPosition" => "Place a thing at an absolute world position and explicitly assign its sector.",
        "setThingOrientation" => "Set absolute pitch, yaw and roll in degrees; read the current orientation using query things.",
        "setThingName" => "Set the thing's display name without changing its template.",
        "setThingTemplate" => "Use an existing template from query templates. Clear an incompatible architecture binding explicitly first.",
        "setThingValue" => "Set one named thing parameter. Inspect values before editing; this is not a COG reference-remapping operation.",
        "clearThingArchitecture" => "Explicitly clear a thing's architectural lighting binding; retain the stored lighting arrays.",
        "setVertexPosition" => "Move a sector-local vertex in world units; all referencing surfaces in that sector use the new position.",
        "setVertexLight" => "Change one stored lighting component on every corner sharing the sector-local vertex. RGB channels require MotS; this does not bake lighting.",
        "setSurfaceMaterial" => "Choose an existing index from query materials; this does not import a new material.",
        "setSurfaceFlags" => "Replace the surface flag bitmask with an unsigned 32-bit value.",
        "setSectorFlags" => "Replace sector flags; collision-box metadata must agree with its presence flag.",
        "setSectorAmbient" => "Set scalar ambient light in [0,1]; read the current value using query sectors.",
        "setGravity" => "Set absolute level gravity; read the current value using query summary.",
        "addBox" => "Append an untextured, unconnected box sector; this does not create a portal or corridor connection.",
        "addThing" => "Append a thing using an existing template from query templates and return its new index in the plan.",
        "addLight" => "Append an editor light; this does not bake surface lighting. Colored lights require MotS.",
        "registerMaterial" => "Append a unique .mat resource basename and return its material index; no asset file is read, verified or copied.",
        "registerColormap" => "Append a unique .cmp resource basename; index zero is the master palette. No asset availability check.",
        "setSectorColormap" => "Select a registered colormap for the sector.",
        "fitSurfaceTexture" => "Fit the surface UVs to the supplied width/height in texels. Query the corners for readback; asset dimensions must be supplied by the caller.",
        "setSurfaceUv" => "Set explicit texel UV pairs in existing surface-corner order. Preserves geometry and independent lighting channels.",
        "setSurfaceRenderModes" => "Set geometry, lighting and texture modes explicitly. Geometry mode zero hides the face; this does not change portal flags.",
        "extrudeSurface" => "Extrude an eligible convex sector face outward by positive distance; append a sector with inward normals and an open paired portal. Refuses occupied portals and overlapping geometry.",
        "adjoinSurfaces" => "Make two coincident, oppositely wound faces in different sectors into an open paired portal. Refuses existing adjoins and mismatched polygons.",
        _ => operation,
    };
}

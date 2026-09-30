using Sed.Core.Editing;
using Sed.Formats.Cogs;
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
        object NullableNumber(double minimum, double maximum) => new { type = new[] { "number", "null" }, minimum, maximum };
        object Index() => new { type = "integer", minimum = 0, maximum = int.MaxValue };
        object Vector(double min, double max) => new { type = "array", minItems = 3, maxItems = 3, items = Number(min, max) };
        object Token() => new { type = "string", minLength = 1, maxLength = 127, pattern = "^[!$-~]+$" };
        var variants = new List<object>();
        foreach (var (operation, fields) in AgentWorkspace.OperationFields)
        {
            if (operationName is not null && operation != operationName) continue;
            var properties = new Dictionary<string, object> { ["op"] = new Dictionary<string, object> { ["const"] = operation } };
            var optional = AgentWorkspace.OptionalOperationFields.TryGetValue(operation, out var optionalFields) ? optionalFields : [];
            foreach (var field in fields.Concat(optional))
                properties[field] = field switch
                {
                    "thing" or "light" or "layer" or "sector" or "surface" or "vertex" or "materialIndex" or "colormapIndex" or "otherSector" or "otherSurface" or "lightingIndex" or "cog" or "parameter" => Index(),
                    "targetIndex" => new { type = "integer", minimum = -1, maximum = int.MaxValue },
                    "kind" => new Dictionary<string, object> { ["enum"] = new[] { "thing", "sector", "surface", "cog" } },
                    "scriptSha256" => new { type = "string", pattern = "^[0-9a-fA-F]{64}$" },
                    "script" => new { type = "string", minLength = 5, maxLength = CogBindings.MaxAuthoredScriptName, pattern = "^(?!.*[/\\\\:])[!$-~]+\\.[cC][oO][gG]$",
                        description = "ASCII .cog basename; path separators and colon are refused." },
                    "bindings" => new { type = "array", minItems = 0, maxItems = CogBindings.MaxAuthoredBindings,
                        items = new { type = "object", additionalProperties = false, required = new[] { "symbol", "kind", "targetIndex" },
                            properties = new { symbol = Token(), kind = new Dictionary<string, object> { ["enum"] = new[] { "thing", "sector", "surface", "cog" } },
                                targetIndex = new { type = "integer", minimum = -1, maximum = int.MaxValue } } } },
                    "frames" => new { type = "array", minItems = 1, maxItems = SetThingFramesCommand.MaxFrames,
                        items = new { type = "object", additionalProperties = false, required = new[] { "position", "orientation" },
                            properties = new { position = Vector(-SetThingFramesCommand.MaxCoordinate, SetThingFramesCommand.MaxCoordinate),
                                orientation = Vector(-SetThingFramesCommand.MaxCoordinate, SetThingFramesCommand.MaxCoordinate) } } },
                    "policy" => new Dictionary<string, object> { ["const"] = "reset" },
                    "profile" => new Dictionary<string, object> { ["enum"] = new[] { "source-points-v1", "source-points-world-occluded-v1" } },
                    "position" or "center" or "orientation" or "origin" => Vector(-1_000_000, 1_000_000),
                    "surfaces" => new { type = "array", minItems = 1, maxItems = AgentWorkspace.MaxBatchUvTargets, uniqueItems = true,
                        items = new { type = "object", additionalProperties = false, required = new[] { "sector", "surface" },
                            properties = new { sector = Index(), surface = Index() } } },
                    "direction" => new Dictionary<string, object> { ["enum"] = new[] { "+x", "-x", "+y", "-y", "+z", "-z" } },
                    "texelsPerUnit" => new { type = "number", exclusiveMinimum = 0, maximum = 1_000_000 },
                    "pivot" or "offset" => new { type = "array", minItems = 2, maxItems = 2, items = Number(-1_000_000, 1_000_000) },
                    "scale" => new { type = "array", minItems = 2, maxItems = 2, items = new Dictionary<string, object> {
                        ["type"] = "number", ["minimum"] = -1_000_000, ["maximum"] = 1_000_000,
                        ["not"] = new Dictionary<string, object> { ["const"] = 0 } } },
                    "rotationDegrees" => Number(-1_000_000, 1_000_000),
                    "layout" => AgentWorkspace.ArrayLayoutSchema(),
                    "namePrefix" => new { type = "string", minLength = 1, maxLength = 123, pattern = "^[!$-~]+$" },
                    "rgb" => Vector(0, 1),
                    "flags" => new { type = "integer", minimum = 0, maximum = (long)uint.MaxValue },
                    "halfSize" or "range" or "distance" => Number(0.0001, 100000),
                    "width" or "height" => new { type = "integer", minimum = 1, maximum = 16384 },
                    "geometryMode" => new { type = "integer", minimum = 0, maximum = 4 },
                    "lightMode" or "textureMode" => new { type = "integer", minimum = 0, maximum = 3 },
                    "uv" => new { type = "array", minItems = 3, maxItems = 64, items = new { type = "array", minItems = 2, maxItems = 2, items = Number(-1_000_000, 1_000_000) } },
                    "intensity" => Number(0, 1000),
                    "rgbIntensity" => NullableNumber(0, 1000),
                    "legacyRgbRange" => NullableNumber(-100000, 100000),
                    "value" when operation == "setGravity" => Number(0, 10000),
                    "value" when operation == "setSectorAmbient" => Number(0, 1),
                    "value" when operation == "setVertexLight" => Number(0, 1000),
                    "channel" => new Dictionary<string, object> { ["enum"] = new[] { "mono", "red", "green", "blue" } },
                    "key" => new { type = "string", minLength = 1, maxLength = 127, pattern = "^[A-Za-z0-9_]+$" },
                    _ => Token(),
                };
            variants.Add(new { type = "object", description = OperationDescription(operation), additionalProperties = false,
                required = new[] { "op" }.Concat(fields).ToArray(), minProperties = operation == "setLight" ? 3 : fields.Length + 1, properties });
            if (AgentSelections.TargetKind(operation) is string targetKind)
            {
                var selectedProperties = new Dictionary<string, object>(properties);
                string[] replaced = ["sector", "surface", "otherSector", "otherSurface"];
                foreach (string field in replaced) selectedProperties.Remove(field);
                selectedProperties["target"] = AgentSelections.TokenSchema(targetKind);
                bool paired = operation is "adjoinSurfaces" or "alignSurfaceTexture";
                if (paired) selectedProperties["otherTarget"] = AgentSelections.TokenSchema("surface");
                string[] selectedRequired = new[] { "op" }.Concat(fields.Where(f => !replaced.Contains(f)))
                    .Concat(paired ? ["target", "otherTarget"] : new[] { "target" }).ToArray();
                variants.Add(new { type = "object", description = OperationDescription(operation)
                    + " Use the unique select result's token instead of integer addressing. Tokens bind the source revision and containing sector geometry, and are checked immediately before this operation.",
                    additionalProperties = false, required = selectedRequired, minProperties = selectedRequired.Length,
                    properties = selectedProperties });
            }
        }
        return new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "SED MotS agent patch version 1",
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new[] { "schemaVersion", "inputSha256", "operations" },
            ["allOf"] = new object[] { new Dictionary<string, object> {
                ["if"] = new { properties = new { operations = new { contains = new {
                    required = new[] { "op" }, properties = new { op = new Dictionary<string, object> {
                        ["enum"] = new[] { "bindThingArchitecture", "rebindThingArchitecture", "bakeThingArchitecture", "rebindCogReference", "addCog" } } } } } } },
                ["then"] = new { required = new[] { "resourceArchives" } },
            } },
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
        "setThingFrames" => "Replace an existing move=path thing's ordered absolute position/orientation frames and numframes together. Uses 1..256 frames within +/-1000000, preserves complete effective parameter records and exact undo. The final touched thing must still resolve move=path, have coherent frame/count records, and be unbound from ARCH. Does not change current position, test collisions or certify engine capacity. Query things sourceParameters for ordered readback.",
        "clearThingArchitecture" => "Explicitly clear a thing's architectural lighting binding; retain the stored lighting arrays.",
        "addCog" => "Append a COG from an exact hash-bound archive script with a complete symbol schema. Supply every external thing/sector/global-surface/placed-COG reference once by name/type; declaration order controls serialization. Existing placed COG scripts also require complete schemas and exact parameter layouts in the explicit archives; append refuses every existing non-static unchecked runtime/template COG reference because it can capture or shift its allocation. Closure inspection is bounded to 4096 existing COGs and 100000 parameters. No inferred defaults or unsupported scalar/resource bindings. Requires resourceArchives and scriptSha256. Returns appended index through plan readback; no script execution.",
        "rebindCogReference" => "Rebind one existing typed thing/sector/global-surface/placed-COG parameter (or -1 null) through the shared undoable command. Requires the exact complete script schema, parameter count, symbol, kind and scriptSha256 from bindings. resourceArchives are mandatory; scripts and other values are preserved. Static/runtime COG links and guessed layouts are refused.",
        "bindThingArchitecture" => "Bind an unbound MotS architecture thing to a private copy of compatible stored samples. Requires resourceArchives; records current model acceptance, not historical bake origin. Read indices using query architecture.",
        "rebindThingArchitecture" => "Explicitly reset this MotS architecture thing to a private zeroed allocation for the captured current model. Requires policy reset and resourceArchives. Retains old arrays; does not bake. Single-LOD static layouts only.",
        "bakeThingArchitecture" => "Bake private MotS ARCH using source-points-v1 (unoccluded) or source-points-world-occluded-v1 (world polygon blockers, BlockLight portals and NoBlock rays), plus resourceArchives. Both use non-directional point-source falloff and independent mono/RGB; neither adds ambient, model/self shadows, alpha-aware material tracing or retail parity. Refuses unsupported LOD/hierarchy/pivot layouts. Saved digests expose stale or overridden inputs; resource freshness requires separate validation.",
        "setVertexPosition" => "Move a sector-local vertex in world units; all referencing surfaces in that sector use the new position.",
        "setVertexLight" => "Change one stored lighting component on every corner sharing the sector-local vertex. RGB channels require MotS; this does not bake lighting.",
        "setSurfaceMaterial" => "Choose an existing index from query materials; this does not import a new material.",
        "setSurfaceFlags" => "Replace the surface flag bitmask with an unsigned 32-bit value.",
        "setSectorFlags" => "Replace sector flags; collision-box metadata must agree with its presence flag.",
        "setSectorAmbient" => "Set scalar ambient light in [0,1]; read the current value using query sectors.",
        "setGravity" => "Set absolute level gravity; read the current value using query summary.",
        "addBox" => "Append an untextured, unconnected box sector; this does not create a portal or corridor connection.",
        "addThing" => "Append a thing using an existing template from query templates and return its new index in the plan.",
        "addLight" => "Append a source light without baking. intensity is white/shared fallback; optional rgbIntensity is independent in MotS (null inherits), legacyRgbRange is preserved metadata only. Optional flags/layer default to zero.",
        "setLight" => "Edit one or more source-light fields through shared undoable commands. Omitted fields stay unchanged; rgbIntensity null restores inheritance and legacyRgbRange null removes only that metadata. Both channels bake with primary range. This operation does not bake lighting.",
        "registerMaterial" => "Append a unique .mat resource basename and return its material index; no asset file is read, verified or copied.",
        "registerColormap" => "Append a unique .cmp resource basename; index zero is the master palette. No asset availability check.",
        "setSectorColormap" => "Select a registered colormap for the sector.",
        "fitSurfaceTexture" => "Fit the surface UVs to the supplied width/height in texels. Query the corners for readback; asset dimensions must be supplied by the caller.",
        "alignSurfaceTexture" => "Align target UVs across a shared edge using otherSector/otherSurface (or otherTarget) as the reference. Preserves reference UVs, geometry, materials and lighting. Refuses non-finite, degenerate or nonplanar geometry and out-of-range results; output UVs are limited to +/-1000000 texels.",
        "setSurfaceUv" => "Set explicit texel UV pairs in existing surface-corner order. Preserves geometry and independent lighting channels.",
        "projectSurfaceUv" => "Project 1..256 explicitly addressed surfaces with one world origin and positive texelsPerUnit. Six directed bases have U cross V equal to direction: +x=(+Y,+Z), -x=(-Y,+Z), +y=(-X,+Z), -y=(+X,+Z), +z=(+X,+Y), -z=(-X,+Y). Refuses edge-on/invalid geometry, duplicate targets and UV overflow. Max4096 addressed corners per patch including repeated edits. Does not fit each face independently or change geometry/lighting/materials.",
        "transformSurfaceUv" => "Transform UVs on 1..256 explicit surfaces about one UV pivot: scale, then counterclockwise rotation in mathematical UV coordinates, then offset. Nonzero scales; UV outputs within +/-1000000. Max4096 addressed corners per patch. Geometry and independent lighting remain unchanged.",
        "addThingArray" => "Append 1..256 new things from an existing template using a deterministic layout. Names are namePrefix_000 etc; conflicts refused. All points must be strictly inside the supplied sector or one unambiguous sector. Existing placed COGs require complete matching schemas in explicit resourceArchives; typed thing bindings must be null -1 or already placed indices. Unknown/future bindings refuse to prevent capture; arbitrary script code is not analyzed. Fixed orientation; no COG/ARCH cloning or capacity growth. Max1024 array outputs per patch. Point containment is not collision clearance or runtime proof.",
        "addLightArray" => "Append 1..256 source lights using a deterministic layout, preserving independent MotS white/RGB and nullable inheritance. Does not bake. Grid U varies fastest; radial samples exclude sweep endpoint and retain fixed light properties. Max1024 array outputs per patch.",
        "setSurfaceRenderModes" => "Set geometry, lighting and texture modes explicitly. Geometry mode zero hides the face; this does not change portal flags.",
        "extrudeSurface" => "Extrude an eligible convex sector face outward by positive distance; append a sector with inward normals and an open paired portal. Refuses occupied portals and overlapping geometry.",
        "adjoinSurfaces" => "Make two coincident, oppositely wound faces in different sectors into an open paired portal. Refuses existing adjoins and mismatched polygons.",
        _ => operation,
    };
}

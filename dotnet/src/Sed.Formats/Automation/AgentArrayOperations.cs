using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Formats.Cogs;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    /// <summary>Validates the bounded layout; used to budget all array outputs before planning.</summary>
    internal static int ArrayOperationCount(JsonElement operation) => ParseArrayLayout(operation).Count;

    private static IReadOnlyList<Vec3> ParseArrayLayout(JsonElement operation)
    {
        if (!operation.TryGetProperty("layout", out var layout)) throw FieldError("Missing required property 'layout'.", "layout");
        try
        {
            string kind = String(layout, "kind");
            switch (kind)
            {
                case "linear":
                    Fields(layout, "kind", "origin", "step", "count");
                    return PlacementArrayLayout.Linear(Vector(layout, "origin"), Vector(layout, "step"), Integer(layout, "count"));
                case "grid":
                    Fields(layout, "kind", "origin", "stepU", "stepV", "countU", "countV");
                    return PlacementArrayLayout.Grid(Vector(layout, "origin"), Vector(layout, "stepU"), Vector(layout, "stepV"),
                        Integer(layout, "countU"), Integer(layout, "countV"));
                case "radial":
                    Fields(layout, "kind", "origin", "axis", "radius", "count", "startDegrees", "sweepDegrees");
                    return PlacementArrayLayout.Radial(Vector(layout, "origin"), Vector(layout, "axis"),
                        Number(layout, "radius", double.Epsilon, PlacementArrayLayout.MaxCoordinate), Integer(layout, "count"),
                        Number(layout, "startDegrees", -PlacementArrayLayout.MaxCoordinate, PlacementArrayLayout.MaxCoordinate),
                        Number(layout, "sweepDegrees", -360, 360));
                default: throw FieldError("layout kind must be linear, grid or radial.", "kind");
            }
        }
        catch (AgentRequestException error)
        {
            throw new AgentRequestException(error.Message, jsonPointer: "/layout" + error.JsonPointer);
        }
        catch (ArgumentException error)
        {
            throw new AgentRequestException(error.Message, jsonPointer: "/layout");
        }
    }

    private static bool TryPrepareArrayOperation(JklDocument document, JsonElement operation,
        ExplicitArchiveResources? resources, out IEditCommand? command)
    {
        command = null;
        string name = String(operation, "op");
        if (name is not ("addThingArray" or "addLightArray")) return false;
        FieldsWithOptional(operation, OptionalOperationFields[name], ["op", .. OperationFields[name]]);
        bool Has(string field) => operation.TryGetProperty(field, out _);
        double? NullableNumber(string field, double min, double max) => operation.GetProperty(field).ValueKind == JsonValueKind.Null
            ? null : Number(operation, field, min, max);
        var level = document.Level;
        var positions = ParseArrayLayout(operation);
        int layer = Has("layer") ? Integer(operation, "layer") : 0;
        if (name == "addThingArray")
        {
            string prefix = Token(operation, "namePrefix");
            if (prefix.Length > 123) throw FieldError("namePrefix allows at most 123 characters, leaving four for _000 through _255.", "namePrefix");
            command = new CreateThingArrayCommand(level, positions, Token(operation, "template"), prefix,
                Has("sector") ? At(level.Sectors, Integer(operation, "sector"), "sector") : null,
                Has("orientation") ? Vector(operation, "orientation") : Vec3.Zero, layer,
                validateReferenceClosure: () => CogThingAppendGuard.Validate(level,
                    resources is null ? null : name => ReadCogScript(resources, name).Script));
        }
        else
        {
            command = new CreateLightArrayCommand(level, positions, Color(operation, "rgb"),
                Number(operation, "intensity", 0, 1000), Number(operation, "range", 0.0001, 100000),
                Has("rgbIntensity") ? NullableNumber("rgbIntensity", 0, 1000) : null,
                Has("legacyRgbRange") ? NullableNumber("legacyRgbRange", -100000, 100000) : null,
                Has("flags") ? UnsignedFlags(operation, "flags") : 0, layer);
        }
        return true;
    }

    private static object ArrayOperationEffect(JklDocument document, JsonElement operation, int? created)
    {
        if (created is not { } start) throw new InvalidOperationException("Array receipts require the captured first created index.");
        int count = ArrayOperationCount(operation);
        var level = document.Level;
        if (String(operation, "op") == "addThingArray")
            return new { count, items = Enumerable.Range(start, count).Select(index =>
            {
                var thing = At(level.Things, index, "thing");
                return new { target = new { kind = "thing", index }, state = new {
                    thing.Name, thing.Template, position = Position(thing.Position), orientation = new[] { thing.Pitch, thing.Yaw, thing.Roll },
                    sector = thing.Sector is null ? -1 : level.Sectors.IndexOf(thing.Sector), thing.Layer, thing.ArchLightingIndex,
                    architecture = DescribeArchitecture(level, thing),
                    values = thing.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { key = pair.Key, value = pair.Value }).ToArray(),
                } };
            }).ToArray() };
        return new { count, items = Enumerable.Range(start, count).Select(index => LightOperationEffect(document, operation, index)).ToArray() };
    }

    public static object ArrayLayoutSchema()
    {
        object Number(double min, double max) => new { type = "number", minimum = min, maximum = max };
        object Vector() => new { type = "array", minItems = 3, maxItems = 3,
            items = Number(-PlacementArrayLayout.MaxCoordinate, PlacementArrayLayout.MaxCoordinate) };
        object Count() => new { type = "integer", minimum = 1, maximum = PlacementArrayLayout.MaxCount };
        object Shape(string kind, params (string Name, object Schema)[] fields)
        {
            var properties = new Dictionary<string, object> { ["kind"] = new Dictionary<string, object> { ["const"] = kind }, ["origin"] = Vector() };
            foreach (var (name, schema) in fields) properties.Add(name, schema);
            return new { type = "object", additionalProperties = false, required = properties.Keys.ToArray(), properties };
        }
        return new { oneOf = new[] {
            Shape("linear", ("step", Vector()), ("count", Count())),
            Shape("grid", ("stepU", Vector()), ("stepV", Vector()), ("countU", Count()), ("countV", Count())),
            Shape("radial", ("axis", Vector()), ("radius", new { type = "number", exclusiveMinimum = 0, maximum = PlacementArrayLayout.MaxCoordinate }),
                ("count", Count()), ("startDegrees", Number(-PlacementArrayLayout.MaxCoordinate, PlacementArrayLayout.MaxCoordinate)),
                ("sweepDegrees", new { type = "number", minimum = -360, maximum = 360, not = new Dictionary<string, object> { ["const"] = 0 } })),
        }, description = "At most 256 distinct generated points within +/-1000000. Grid U changes fastest; countU*countV <=256. Radial U projects the least-aligned cardinal axis (X/Y/Z tie order) perpendicular to the normalized axis, V=axis cross U; angle=startDegrees+sweepDegrees*i/count, endpoint excluded, including partial sweeps. Axis must be nonzero. No automatic thing rotation." };
    }
}

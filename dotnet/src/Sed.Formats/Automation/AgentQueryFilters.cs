using System.Text.Json;
using Sed.Core.Model;

namespace Sed.Formats.Automation;

/// <summary>Collection-specific, exact predicates over stored editor values.</summary>
internal sealed class AgentQueryFilter
{
    private static readonly string[] SurfaceFields = ["materialIndex", "layer", "surfFlags", "faceFlags", "adjoinFlags"];
    private static readonly string[] ThingFields = ["name", "template", "layer"];
    private static readonly string[] LayerFlagsFields = ["layer", "flags"];
    internal static readonly string[] Collections = ["surfaces", "things", "thingContainment", "sectors", "lights"];

    internal JsonElement Json { get; }
    internal int? MaterialIndex { get; private init; }
    private int? Layer { get; init; }
    private string? Name { get; init; }
    private string? Template { get; init; }
    private FlagPredicate? Flags { get; init; }
    private FlagPredicate? SurfFlags { get; init; }
    private FlagPredicate? FaceFlags { get; init; }
    private FlagPredicate? AdjoinFlags { get; init; }

    private AgentQueryFilter(JsonElement json) => Json = json.Clone();

    private sealed record FlagPredicate(uint? Any, uint? All, uint? None)
    {
        internal bool Matches(long value) =>
            (!Any.HasValue || (value & Any.Value) != 0) &&
            (!All.HasValue || (value & All.Value) == All.Value) &&
            (!None.HasValue || (value & None.Value) == 0);
    }

    private static string[] Fields(string collection) => collection switch
    {
        "surfaces" => SurfaceFields,
        "things" or "thingContainment" => ThingFields,
        "sectors" or "lights" => LayerFlagsFields,
        _ => throw new AgentRequestException($"filter is not supported for the '{collection}' collection."),
    };

    internal static object Schema(string collection)
    {
        object text = new { type = "string", minLength = 1, maxLength = 127, pattern = @"\S",
            description = "Exact, ordinal case-insensitive equality; whitespace is significant and no path normalization is applied." };
        object mask = new { type = "integer", minimum = 0, maximum = uint.MaxValue };
        object flags = new Dictionary<string, object>
        {
            ["type"] = "object", ["additionalProperties"] = false, ["minProperties"] = 1,
            ["description"] = "Unsigned 32-bit masks, combined with AND: any requires at least one bit; all requires every bit; none forbids every listed bit. any:0 matches nothing; all:0 and none:0 match every value. Tests use stored flags, not inferred engine behavior.",
            ["properties"] = new Dictionary<string, object> { ["any"] = mask, ["all"] = mask, ["none"] = mask },
        };
        var properties = new Dictionary<string, object>();
        foreach (string field in Fields(collection))
            properties[field] = field switch
            {
                "materialIndex" => new { type = "integer", minimum = -1, maximum = int.MaxValue,
                    description = "Exact material-table slot; -1 selects untextured surfaces. Nonnegative indices must exist in the loaded material table." },
                "layer" => new { type = "integer", minimum = 0, maximum = int.MaxValue,
                    description = "Exact stored editor layer number; surfaces use their owning sector's layer. A number without matching objects returns an empty page." },
                "name" or "template" => text,
                _ => flags,
            };
        return new Dictionary<string, object>
        {
            ["type"] = "object", ["additionalProperties"] = false, ["minProperties"] = 1,
            ["description"] = "All supplied predicates must match, in addition to search. Original source indices are retained; offset counts filtered matches. An adjoinFlags predicate requires an actual adjoin; unadjoined surfaces do not match, even with all:0 or none:0.",
            ["properties"] = properties,
        };
    }

    internal static AgentQueryFilter Parse(JsonElement json, string collection)
    {
        CheckProperties(json, Fields(collection), "filter");
        return new AgentQueryFilter(json)
        {
            MaterialIndex = ReadInteger(json, "materialIndex", -1),
            Layer = ReadInteger(json, "layer", 0),
            Name = ReadText(json, "name"), Template = ReadText(json, "template"),
            Flags = ReadFlags(json, "flags"), SurfFlags = ReadFlags(json, "surfFlags"),
            FaceFlags = ReadFlags(json, "faceFlags"), AdjoinFlags = ReadFlags(json, "adjoinFlags"),
        };
    }

    private static void CheckProperties(JsonElement json, string[] allowed, string label)
    {
        if (json.ValueKind != JsonValueKind.Object)
            throw new AgentRequestException($"{label} must be a nonempty JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in json.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new AgentRequestException($"Duplicate or unsupported {label} property '{property.Name}'.");
        if (names.Count == 0) throw new AgentRequestException($"{label} must contain at least one predicate.");
    }

    private static int? ReadInteger(JsonElement json, string name, int minimum)
    {
        if (!json.TryGetProperty(name, out var field)) return null;
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out int result) || result < minimum)
            throw new AgentRequestException($"filter.{name} must be an integer between {minimum} and {int.MaxValue}.");
        return result;
    }

    private static string? ReadText(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var field)) return null;
        if (field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()) || field.GetString()!.Length > 127)
            throw new AgentRequestException($"filter.{name} must contain between 1 and 127 characters and cannot be blank.");
        return field.GetString();
    }

    private static FlagPredicate? ReadFlags(JsonElement json, string name)
    {
        if (!json.TryGetProperty(name, out var flags)) return null;
        CheckProperties(flags, ["any", "all", "none"], $"filter.{name}");
        uint? ReadMask(string key)
        {
            if (!flags.TryGetProperty(key, out var field)) return null;
            if (field.ValueKind != JsonValueKind.Number || !field.TryGetUInt32(out uint result))
                throw new AgentRequestException($"filter.{name}.{key} must be an integer between 0 and {uint.MaxValue}.");
            return result;
        }
        return new FlagPredicate(ReadMask("any"), ReadMask("all"), ReadMask("none"));
    }

    private bool MatchesLayer(int value) => !Layer.HasValue || Layer.Value == value;
    internal bool Matches(Sector sector) => MatchesLayer(sector.Layer) && (Flags?.Matches(sector.Flags) ?? true);
    internal bool Matches(Light light) => MatchesLayer(light.Layer) && (Flags?.Matches(light.Flags) ?? true);
    internal bool Matches(Thing thing) => MatchesLayer(thing.Layer) &&
        (Name is null || string.Equals(thing.Name, Name, StringComparison.OrdinalIgnoreCase)) &&
        (Template is null || string.Equals(thing.Template, Template, StringComparison.OrdinalIgnoreCase));
    internal bool Matches(Surface surface) => MatchesLayer(surface.Sector.Layer) &&
        (!MaterialIndex.HasValue || surface.MaterialIndex == MaterialIndex.Value) &&
        (SurfFlags?.Matches(surface.SurfFlags) ?? true) &&
        (FaceFlags?.Matches(surface.FaceFlags) ?? true) &&
        (AdjoinFlags is null || (surface.Adjoin is not null && AdjoinFlags.Matches(surface.AdjoinFlags)));
}

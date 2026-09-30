using Sed.Core.Model;
using Sed.Core.Validation;

namespace Sed.Formats.Cogs;

/// <summary>
/// Refuses thing append when a placed COG's external typed thing parameter could
/// acquire a new meaning. Only -1 and currently placed thing indices are proven
/// stable. Future/runtime slots are not inferred from declared thing capacity.
/// This checks positional symbols, not arbitrary script code, script-local
/// constants, dynamically computed indices or scripts attached through templates.
/// Resource identity and hash capture belong to the caller. Invoke again before
/// every append; no result or schema cache survives a call.
/// </summary>
public static class CogThingAppendGuard
{
    public const int MaxCogs = 4096;
    public const int MaxParameters = 100_000;

    public static void Validate(Level level, Func<string, CogScript?>? resolver)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (level.Cogs.Count > MaxCogs)
            throw new CogBindingException("thingAppendCogBudget", $"Thing append can inspect at most {MaxCogs} placed COGs.");
        int parameters = 0;
        foreach (var cog in level.Cogs)
        {
            if (cog is null || string.IsNullOrWhiteSpace(cog.Name))
                throw new CogBindingException("incompleteExistingCogSchema", "Every existing placed COG needs a script name and matching complete symbol schema before thing append.");
            if (cog.Values.Count > MaxParameters - parameters)
                throw new CogBindingException("thingAppendCogBudget", $"Thing append cannot inspect more than {MaxParameters} existing COG parameters.");
            parameters += cog.Values.Count;
        }

        var schemas = new Dictionary<string, CogScript?>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < level.Cogs.Count; index++)
        {
            var cog = level.Cogs[index];
            if (!schemas.TryGetValue(cog.Name, out var schema))
                schemas[cog.Name] = schema = resolver?.Invoke(cog.Name);
            if (schema is null || !schema.IsComplete || !SameScript(cog.Name, schema.Name))
                throw new CogBindingException("incompleteExistingCogSchema",
                    $"Existing COG {index} ('{cog.Name}') needs a matching complete script symbol schema before thing append. Supply the script through the caller's verified resources; raw values are not guessed to be thing references.");
            if (schema.LevelValues.Count != cog.Values.Count)
                throw new CogBindingException("existingCogParameterCountMismatch",
                    $"Existing COG {index} ('{cog.Name}') has {cog.Values.Count} values but its schema requires {schema.LevelValues.Count}. Resolve that layout explicitly before thing append.");

            for (int parameter = 0; parameter < cog.Values.Count; parameter++)
            {
                var symbol = schema.LevelValues[parameter];
                if (symbol.Type != CogSymbolType.Thing) continue;
                string? raw = cog.Values[parameter];
                if (raw is null || !CogReferenceRules.TryIndex(raw, out int target)
                    || target < -1 || (target == -1 && raw.Trim() != "-1"))
                    throw new CogBindingException("invalidExistingThingReference",
                        $"Existing COG {index}, parameter {parameter} ('{symbol.Name}'), raw value '{raw}', needs a complete decimal placed-thing index or the canonical -1 null before thing append.");
                if (target == -1) continue;
                // Unlike COG references, thing references have no static-world
                // 0x8000 exception here. Engine PopThing addresses current-world
                // slots, including slots that may be allocated at runtime.
                if (target >= level.Things.Count)
                    throw new CogBindingException("existingThingRuntimeReference",
                        $"Existing COG {index}, parameter {parameter} ('{symbol.Name}'), raw value '{raw}', is outside the {level.Things.Count} currently placed things. Appending can capture an unplaced slot or change future allocation; resolve the reference explicitly instead of guessing its runtime target.");
            }
        }
    }

    private static bool SameScript(string expected, string actual)
        => string.Equals(expected.Replace('\\', '/'), actual.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

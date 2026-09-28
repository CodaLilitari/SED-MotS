using System.Globalization;
using System.Text.Json;
using Sed.Core.Editing;
using Sed.Formats.Jkl;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    private static IEditCommand PrepareThingFrames(JklDocument document, JsonElement operation)
    {
        Fields(operation, ["op", .. OperationFields["setThingFrames"]]);
        var rows = operation.GetProperty("frames");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > SetThingFramesCommand.MaxFrames)
            throw FieldError($"frames must contain 1 through {SetThingFramesCommand.MaxFrames} absolute position/orientation pairs.", "frames");
        var frames = new List<ThingFrame>();
        int index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            try
            {
                Fields(row, "position", "orientation");
                frames.Add(new(Vector(row, "position"), Vector(row, "orientation")));
            }
            catch (AgentRequestException error)
            {
                throw new AgentRequestException(error.Message, jsonPointer: $"/frames/{index}" + error.JsonPointer);
            }
            index++;
        }
        return new SetThingFramesCommand(document.Level, At(document.Level.Things, Integer(operation, "thing"), "thing"), frames);
    }

    private static object ThingFramesEffect(JklDocument document, JsonElement operation)
    {
        int index = Integer(operation, "thing");
        var thing = At(document.Level.Things, index, "thing");
        var records = ParameterRecords.Merge(thing.SourceParameters, thing.Values).ToArray();
        return new { target = new { kind = "thing", index }, state = new {
            pathParameters = records
                .Where(p => p.Key.Equals("frame", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("numframes", StringComparison.OrdinalIgnoreCase))
                .Select(p => new { key = p.Key, value = p.Value }).ToArray(),
            effectiveParameters = records.Select(p => new { key = p.Key, value = p.Value }).ToArray(),
            values = thing.Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p => new { key = p.Key.ToLowerInvariant(), value = p.Value }).ToArray() } };
    }

    private static void ValidateFinalThingFrames(JklDocument document, IReadOnlyList<JsonElement> operations)
    {
        var checkedThings = new HashSet<int>();
        for (int index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            if (String(operation, "op") != "setThingFrames") continue;
            int thingIndex = Integer(operation, "thing");
            if (!checkedThings.Add(thingIndex)) continue;
            try
            {
                var thing = At(document.Level.Things, thingIndex, "thing");
                var records = ParameterRecords.Merge(thing.SourceParameters, thing.Values).ToArray();
                var counts = records.Where(p => p.Key.Equals("numframes", StringComparison.OrdinalIgnoreCase)).ToArray();
                var frames = records.Where(p => p.Key.Equals("frame", StringComparison.OrdinalIgnoreCase))
                    .Select(p => ParseAuthoredFrame(p.Value)).ToArray();
                if (counts.Length != 1 || !int.TryParse(counts[0].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int count) || count != frames.Length)
                    throw new ArgumentException("Final authored numframes must match the complete ordered frame count.");
                // Reuse the shared command's eligibility and finite/budget checks,
                // without applying it. Later edits may repair an intermediate state,
                // but may not leave this authored path non-path or ARCH-bound.
                _ = new SetThingFramesCommand(document.Level, thing, frames);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
            {
                throw new AgentRequestException($"Final state of frame-authored thing {thingIndex}: {error.Message}",
                    index, "setThingFrames", $"/operations/{index}", error);
            }
        }
    }

    private static ThingFrame ParseAuthoredFrame(string value)
    {
        if (value.Length < 5 || value[0] != '(' || value[^1] != ')')
            throw new FormatException("Final authored frame must be (x/y/z:pitch/yaw/roll).");
        var halves = value[1..^1].Split(':');
        if (halves.Length != 2) throw new FormatException("Final authored frame needs position and orientation.");
        var position = halves[0].Split('/'); var orientation = halves[1].Split('/');
        if (position.Length != 3 || orientation.Length != 3)
            throw new FormatException("Final authored frame needs three position and three orientation components.");
        double N(string text) => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return new(new(N(position[0]), N(position[1]), N(position[2])),
            new(N(orientation[0]), N(orientation[1]), N(orientation[2])));
    }
}

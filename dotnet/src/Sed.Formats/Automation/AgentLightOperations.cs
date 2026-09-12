using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    public static readonly IReadOnlyDictionary<string, string[]> OptionalOperationFields = new Dictionary<string, string[]>
    {
        ["addLight"] = ["rgbIntensity", "legacyRgbRange", "flags", "layer"],
        ["setLight"] = ["position", "rgb", "intensity", "range", "rgbIntensity", "legacyRgbRange", "flags", "layer"],
    };

    private static bool TryPrepareLightOperation(JklDocument doc, JsonElement op, out IEditCommand? command)
    {
        command = null;
        string name = String(op, "op");
        if (name is not ("addLight" or "setLight")) return false;
        FieldsWithOptional(op, OptionalOperationFields[name], ["op", .. OperationFields[name]]);
        bool Has(string field) => op.TryGetProperty(field, out _);
        double? NullableNumber(string field, double min, double max) => op.GetProperty(field).ValueKind == JsonValueKind.Null
            ? null : Number(op, field, min, max);
        int Layer()
        {
            int value = Integer(op, "layer");
            if (value < 0 || value >= System.Math.Max(1, doc.Level.Layers.Count))
                throw FieldError("layer must name an existing editor layer (0 is the default when no layers are named).", "layer");
            return value;
        }
        var rgb = Has("rgb") ? Color(op, "rgb") : ColorF.White;
        var rgbIntensity = Has("rgbIntensity") ? NullableNumber("rgbIntensity", 0, 1000) : null;
        if (doc.Level.Kind != ProjectType.MysteriesOfTheSith)
        {
            if (Has("rgb") && rgb != ColorF.White)
                throw FieldError("Colored source-light authoring requires a MotS level. Existing imported metadata remains preserved.", "rgb");
            if (rgbIntensity.HasValue)
                throw FieldError("Independent RGB source-light authoring requires a MotS level; null restores inherited intensity.", "rgbIntensity");
        }
        var legacyRange = Has("legacyRgbRange") ? NullableNumber("legacyRgbRange", -100000, 100000) : null;
        if (name == "addLight")
        {
            command = new CreateLightCommand(doc.Level, new Light
            {
                Position = Vector(op, "position"), Color = rgb,
                Intensity = Number(op, "intensity", 0, 1000), Range = Number(op, "range", 0.0001, 100000),
                RgbIntensity = rgbIntensity, LegacyRgbRange = legacyRange,
                Flags = Has("flags") ? UnsignedFlags(op, "flags") : 0,
                Layer = Has("layer") ? Layer() : 0,
            });
            return true;
        }
        if (!OptionalOperationFields[name].Any(Has))
            throw new AgentRequestException("setLight requires at least one source-light field to edit.");
        var light = At(doc.Level.Lights, Integer(op, "light"), "light");
        var changes = new List<IEditCommand>();
        if (Has("position")) changes.Add(new SetLightPositionCommand(light, Vector(op, "position")));
        if (Has("rgb")) changes.Add(new SetLightColorCommand(light, rgb));
        if (Has("intensity")) changes.Add(new SetLightIntensityCommand(light, Number(op, "intensity", 0, 1000)));
        if (Has("range")) changes.Add(new SetLightRangeCommand(light, Number(op, "range", 0.0001, 100000)));
        if (Has("rgbIntensity")) changes.Add(new SetLightRgbIntensityCommand(light, rgbIntensity));
        if (Has("legacyRgbRange")) changes.Add(new SetLightLegacyRgbRangeCommand(light, legacyRange));
        if (Has("flags")) changes.Add(new SetLightFlagsCommand(light, UnsignedFlags(op, "flags")));
        if (Has("layer")) changes.Add(new SetLightLayerCommand(light, Layer()));
        command = new CompositeCommand("Edit source light", changes);
        return true;
    }

    private static object LightOperationEffect(JklDocument doc, JsonElement op, int? created)
    {
        int index = created ?? Integer(op, "light");
        var light = At(doc.Level.Lights, index, "light");
        return new { target = new { kind = "light", index }, state = new
        {
            position = Position(light.Position), rgb = new[] { light.Color.R, light.Color.G, light.Color.B },
            light.Intensity, light.Range, light.RgbIntensity, light.EffectiveRgbIntensity, light.LegacyRgbRange,
            rgbIntensityInherited = !light.RgbIntensity.HasValue, light.Flags, light.Layer,
        } };
    }
}

using System.Globalization;
using Avalonia.Controls;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.App;

public static class LightInspector
{
    public static Control Build(Light light, EditHistory history, InspectorContext? context = null)
    {
        var p = InspectorPanel.Panel($"Light {light.Num}");
        bool mots = context?.Level?.Kind == ProjectType.MysteriesOfTheSith;

        p.Children.Add(InspectorPanel.Row("Flags",
            InspectorPanel.TextField($"0x{light.Flags:x}",
                text =>
                {
                    var style = NumberStyles.HexNumber;
                    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    {
                        style = NumberStyles.AllowHexSpecifier;
                        text = text[2..];
                    }
                    if (long.TryParse(text, style, CultureInfo.InvariantCulture, out var flags) && flags != light.Flags)
                        history.Do(new SetLightFlagsCommand(light, flags));
                })));

        p.Children.Add(InspectorPanel.Row("Range",
            ExactNumber(light.Range,
                v => history.Do(new SetLightRangeCommand(light, v)))));

        p.Children.Add(InspectorPanel.Row(mots ? "White intensity" : "Intensity",
            ExactNumber(light.Intensity,
                v => history.Do(new SetLightIntensityCommand(light, v)))));

        if (mots)
        {
            var rgbField = ExactNumber(light.EffectiveRgbIntensity,
                v => history.Do(new SetLightRgbIntensityCommand(light, v)));
            rgbField.IsEnabled = light.RgbIntensity.HasValue;
            var linked = InspectorPanel.CheckField("RGB follows white", !light.RgbIntensity.HasValue, value =>
            {
                double? next = value ? null : light.EffectiveRgbIntensity;
                if (next != light.RgbIntensity) history.Do(new SetLightRgbIntensityCommand(light, next));
                rgbField.IsEnabled = !value;
                rgbField.Text = light.EffectiveRgbIntensity.ToString("R", CultureInfo.InvariantCulture);
            });
            ToolTip.SetTip(linked, "Linked lights share the white intensity. Unlink to author a separate RGB intensity, including zero.");
            p.Children.Add(linked);
            p.Children.Add(InspectorPanel.Row("RGB intensity", rgbField));
        }

        if (mots || light.LegacyRgbRange.HasValue)
        {
            var legacy = OptionalNumber(light.LegacyRgbRange,
                v => history.Do(new SetLightLegacyRgbRangeCommand(light, v)));
            ToolTip.SetTip(legacy, "Preserved legacy RGB-range metadata. Both lighting channels use the primary Range. Blank removes this metadata.");
            p.Children.Add(InspectorPanel.Row("Legacy range", legacy));
        }

        p.Children.Add(InspectorPanel.Row("Color R",
            ExactNumber(light.Color.R,
                v => history.Do(new SetLightColorCommand(light, new ColorF((float)v, light.Color.G, light.Color.B))))));
        p.Children.Add(InspectorPanel.Row("Color G",
            ExactNumber(light.Color.G,
                v => history.Do(new SetLightColorCommand(light, new ColorF(light.Color.R, (float)v, light.Color.B))))));
        p.Children.Add(InspectorPanel.Row("Color B",
            ExactNumber(light.Color.B,
                v => history.Do(new SetLightColorCommand(light, new ColorF(light.Color.R, light.Color.G, (float)v))))));

        p.Children.Add(InspectorPanel.Row("X",
            ExactNumber(light.Position.X,
                v => history.Do(new SetLightPositionCommand(light, new Vec3(v, light.Position.Y, light.Position.Z))))));
        p.Children.Add(InspectorPanel.Row("Y",
            ExactNumber(light.Position.Y,
                v => history.Do(new SetLightPositionCommand(light, new Vec3(light.Position.X, v, light.Position.Z))))));
        p.Children.Add(InspectorPanel.Row("Z",
            ExactNumber(light.Position.Z,
                v => history.Do(new SetLightPositionCommand(light, new Vec3(light.Position.X, light.Position.Y, v))))));

        p.Children.Add(InspectorPanel.Row("Layer",
            ExactNumber(light.Layer,
                v => { if (v == System.Math.Truncate(v) && v >= int.MinValue && v <= int.MaxValue)
                    history.Do(new SetLightLayerCommand(light, (int)v)); })));

        return p;
    }

    // Showing or leaving a light field must not round source values or add an undo entry.
    private static TextBox ExactNumber(double value, Action<double> onCommit)
    {
        double last = value;
        return InspectorPanel.TextField(value.ToString("R", CultureInfo.InvariantCulture), text =>
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double next)
                && double.IsFinite(next) && next != last)
            {
                double previous = last;
                // History.Changed can synchronously replace this focused control.
                last = next;
                try { onCommit(next); }
                catch { last = previous; throw; }
            }
        });
    }

    private static TextBox OptionalNumber(double? value, Action<double?> onCommit)
    {
        double? last = value;
        return InspectorPanel.TextField(value?.ToString("R", CultureInfo.InvariantCulture) ?? "", text =>
        {
            double? next = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    || !double.IsFinite(number)) return;
                next = number;
            }
            if (next != last)
            {
                double? previous = last;
                last = next;
                try { onCommit(next); }
                catch { last = previous; throw; }
            }
        });
    }
}

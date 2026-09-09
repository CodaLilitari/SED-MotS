using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.App;

public static class ThingInspector
{
    public static Control Build(Thing thing, EditHistory history, InspectorContext? context = null)
    {
        var p = InspectorPanel.Panel($"Thing {thing.Num}");

        p.Children.Add(InspectorPanel.Row("Name",
            InspectorPanel.TextField(thing.Name, v => history.Do(new SetThingNameCommand(thing, v)))));

        p.Children.Add(InspectorPanel.Row("Template",
            PickerField.Build(context?.Owner, "Template", thing.Template,
                () => context?.Level is { } lvl ? PickerField.Templates(lvl) : Array.Empty<PickerItem>(),
                v => history.Do(new SetThingTemplateCommand(thing, v)))));

        if (thing.ArchLightingIndex >= 0)
        {
            p.Children.Add(new TextBlock
            {
                Text = $"Baked lighting #{thing.ArchLightingIndex} is preserved from the original model.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6), FontSize = 11,
            });
            var unbind = new Button { Content = "Unbind baked lighting", Margin = new Thickness(4, 0, 4, 6) };
            ToolTip.SetTip(unbind, "Use before replacing the model. The old lighting arrays remain available and Undo restores the binding.");
            unbind.Click += (_, _) => history.Do(new ClearThingArchitectureCommand(thing));
            p.Children.Add(unbind);
        }

        int sectorNum = thing.Sector?.Num ?? -1;
        p.Children.Add(InspectorPanel.Row("Sector",
            InspectorPanel.NumericField(sectorNum, v =>
            {
                int idx = (int)v;
                Sector? sec = null;
                if (thing.Level is not null && (uint)idx < (uint)thing.Level.Sectors.Count)
                    sec = thing.Level.Sectors[idx];
                history.Do(new SetThingSectorCommand(thing, sec));
            })));

        p.Children.Add(InspectorPanel.Row("X",
            InspectorPanel.NumericField(thing.Position.X, v =>
                history.Do(new SetThingPositionCommand(thing, new Vec3(v, thing.Position.Y, thing.Position.Z))))));
        p.Children.Add(InspectorPanel.Row("Y",
            InspectorPanel.NumericField(thing.Position.Y, v =>
                history.Do(new SetThingPositionCommand(thing, new Vec3(thing.Position.X, v, thing.Position.Z))))));
        p.Children.Add(InspectorPanel.Row("Z",
            InspectorPanel.NumericField(thing.Position.Z, v =>
                history.Do(new SetThingPositionCommand(thing, new Vec3(thing.Position.X, thing.Position.Y, v))))));

        p.Children.Add(InspectorPanel.Row("Pitch",
            InspectorPanel.NumericField(thing.Pitch, v =>
                history.Do(new SetThingOrientationCommand(thing, v, thing.Yaw, thing.Roll)))));
        p.Children.Add(InspectorPanel.Row("Yaw",
            InspectorPanel.NumericField(thing.Yaw, v =>
                history.Do(new SetThingOrientationCommand(thing, thing.Pitch, v, thing.Roll)))));
        p.Children.Add(InspectorPanel.Row("Roll",
            InspectorPanel.NumericField(thing.Roll, v =>
                history.Do(new SetThingOrientationCommand(thing, thing.Pitch, thing.Yaw, v)))));

        p.Children.Add(InspectorPanel.Row("Layer",
            InspectorPanel.NumericField(thing.Layer, v => history.Do(new SetThingLayerCommand(thing, (int)v)))));

        if (thing.Values.Count > 0)
        {
            p.Children.Add(new TextBlock
            {
                Text = "Params",
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(4, 8, 4, 2),
                Foreground = Brushes.White,
            });
            foreach (var kv in thing.Values)
            {
                var key = kv.Key;
                p.Children.Add(InspectorPanel.Row(key,
                    InspectorPanel.TextField(kv.Value, v => history.Do(new SetThingValueCommand(thing, key, v)))));
            }
        }

        return p;
    }
}

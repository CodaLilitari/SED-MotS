using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.App;

public static class SectorInspector
{
    public static Control Build(Sector sector, EditHistory history)
    {
        var p = InspectorPanel.Panel($"Sector {sector.Num}");

        p.Children.Add(InspectorPanel.Row("Flags", InspectorPanel.TextField(
            $"0x{sector.Flags:x}", text =>
            {
                if (long.TryParse(text.TrimStart("0x".ToCharArray()),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags))
                    history.Do(new SetSectorFlagsCommand(sector, flags));
            })));

        p.Children.Add(InspectorPanel.Row("Ambient", InspectorPanel.NumericField(
            sector.Ambient.R, v =>
                history.Do(new SetSectorAmbientCommand(sector, new ColorF((float)v, (float)v, (float)v))))));

        p.Children.Add(InspectorPanel.Row("Extra Light", InspectorPanel.NumericField(
            sector.ExtraLight.R, v =>
                history.Do(new SetSectorExtraLightCommand(sector, new ColorF((float)v, (float)v, (float)v))))));

        p.Children.Add(InspectorPanel.Row("Tint R", InspectorPanel.NumericField(
            sector.Tint.R, v =>
                history.Do(new SetSectorTintCommand(sector, new ColorF((float)v, sector.Tint.G, sector.Tint.B))))));

        p.Children.Add(InspectorPanel.Row("Tint G", InspectorPanel.NumericField(
            sector.Tint.G, v =>
                history.Do(new SetSectorTintCommand(sector, new ColorF(sector.Tint.R, (float)v, sector.Tint.B))))));

        p.Children.Add(InspectorPanel.Row("Tint B", InspectorPanel.NumericField(
            sector.Tint.B, v =>
                history.Do(new SetSectorTintCommand(sector, new ColorF(sector.Tint.R, sector.Tint.G, (float)v))))));

        AddColormapFields(p, sector, history);

        p.Children.Add(InspectorPanel.Row("Sound", InspectorPanel.TextField(
            sector.Sound, text => history.Do(new SetSectorSoundCommand(sector, text, sector.SoundVolume)))));

        p.Children.Add(InspectorPanel.Row("Sound Vol", InspectorPanel.NumericField(
            sector.SoundVolume, v => history.Do(new SetSectorSoundCommand(sector, sector.Sound, v)))));

        p.Children.Add(InspectorPanel.Row("Layer", InspectorPanel.NumericField(
            sector.Layer, v => history.Do(new SetSectorLayerCommand(sector, (int)v)))));

        return p;
    }

    private static void AddColormapFields(StackPanel panel, Sector sector, EditHistory history)
    {
        var error = new TextBlock
        {
            Name = "SectorColormapError", Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Margin = new Thickness(4, 0, 4, 4), IsVisible = false,
        };
        bool committing = false;
        string displayedName = sector.ColorMap;
        TextBox? nameField = null;

        void Commit(string name, int? index = null)
        {
            if (committing) return;
            // The captured field value also protects against a focus-loss event
            // from a control being replaced synchronously during undo/redo.
            if (index is null && name == displayedName ||
                index.HasValue && name == sector.ColorMap && index.Value == sector.ColorMapIndex)
            {
                error.IsVisible = false;
                return;
            }
            SetSectorColormapCommand command;
            try { command = new SetSectorColormapCommand(sector, name, index); }
            catch (Exception refused) when (refused is InvalidOperationException or ArgumentException)
            {
                error.Text = refused.Message;
                error.IsVisible = true;
                return;
            }

            committing = true;
            try
            {
                displayedName = name;
                if (nameField is not null) nameField.Text = name;
                error.IsVisible = false;
                history.Do(command);
            }
            finally { committing = false; }
        }

        nameField = InspectorPanel.TextField(displayedName, text => Commit(text));
        panel.Children.Add(InspectorPanel.Row("Colormap", nameField));

        var names = sector.Level.ColorMaps;
        int selected = sector.ColorMapIndex;
        if ((uint)selected >= (uint)names.Count || !names[selected].Equals(sector.ColorMap, StringComparison.OrdinalIgnoreCase))
        {
            var matches = names.Select((name, index) => (name, index))
                .Where(item => item.name.Equals(sector.ColorMap, StringComparison.OrdinalIgnoreCase)).ToArray();
            selected = matches.Length == 1 ? matches[0].index : -1;
        }
        var slots = new ComboBox
        {
            Name = "SectorColormapSlots", FontSize = 11, MinWidth = 110,
            ItemsSource = names.Select((name, index) => $"{index}: {name}").ToArray(),
            SelectedIndex = selected, IsEnabled = names.Count > 0,
        };
        ToolTip.SetTip(slots, "Choose a registered colormap by its exact table slot. Repeated names can identify different slots.");
        // Subscribe after initialization so opening/rebuilding the inspector is
        // read-only, including when a previously selected duplicate is shown.
        slots.SelectionChanged += (_, _) =>
        {
            int index = slots.SelectedIndex;
            if ((uint)index < (uint)sector.Level.ColorMaps.Count)
                Commit(sector.Level.ColorMaps[index], index);
        };
        panel.Children.Add(InspectorPanel.Row("Registered CMP", slots));
        panel.Children.Add(error);
    }
}

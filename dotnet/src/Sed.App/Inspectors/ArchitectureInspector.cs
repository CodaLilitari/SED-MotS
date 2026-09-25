using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Model;
using Sed.Formats.Resources;

namespace Sed.App;

/// <summary>Explicit resource-aware ARCH edits, using the same service as agent authoring.</summary>
internal static class ArchitectureInspector
{
    public static void Add(StackPanel panel, Thing thing, EditHistory history, InspectorContext? context)
    {
        var level = thing.Level ?? context?.Level;
        if (level is null || level.Kind != ProjectType.MysteriesOfTheSith) return;
        var status = new TextBlock
        {
            Name = "ArchitectureStatus", FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 6),
        };
        void RefreshStatus()
        {
            try
            {
                status.Text = $"Architectural lighting: {ArchitecturalLightingAuthoring.Status(level, thing)}" +
                    (thing.ArchLightingIndex >= 0 ? $" (object #{thing.ArchLightingIndex})" : "") +
                    ". Status describes document inputs; current resources are checked when preparing an action or exporting.";
            }
            catch (InvalidOperationException unavailable)
            { status.Text = "Architectural lighting: unavailable. " + unavailable.Message; }
        }
        RefreshStatus();
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock
        {
            Text = "Choose unoccluded source lights or world polygon shadows. Both use distance falloff without directional shading, model/self shadows or texture-alpha tracing. Render/fullbright modes do not make walls transmit light. Reset creates zero samples; these recipes do not certify retail parity.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 4),
        });
        var error = new TextBlock
        {
            Name = "ArchitectureError", Foreground = Brushes.OrangeRed,
            TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Margin = new Thickness(4, 0, 4, 4), IsVisible = false,
        };
        var slots = new ComboBox { Name = "ArchitectureSlots", MinWidth = 130,
            ItemsSource = level.ArchitecturalLighting.Select((item, index) =>
                $"{index}: {item.Meshes.Count} meshes, {item.Meshes.Sum(mesh => mesh.Vertices.Count)} samples").ToArray(),
            SelectedIndex = level.ArchitecturalLighting.Count > 0 ? 0 : -1 };
        panel.Children.Add(InspectorPanel.Row("Existing lighting", slots));
        bool committing = false;
        void Commit(string action, string profile = ArchitecturalLightingAuthoring.BakeProfile)
        {
            if (committing) return;
            committing = true;
            try
            {
                IEditCommand command;
                if (action == "unbind") command = new ClearThingArchitectureCommand(thing);
                else
                {
                    using var resources = context?.CaptureArchitectureResources?.Invoke()
                        ?? throw new InvalidOperationException("Configure the game's resource archives before authoring architectural lighting.");
                    int? index = action == "bind" ? slots.SelectedIndex : null;
                    if (index < 0) throw new InvalidOperationException("Select an existing architectural-lighting object to bind.");
                    command = ArchitectureAuthoringService.Prepare(level, thing, resources, action, index, profile);
                    resources.VerifyUnchanged();
                    // Keep the capture alive through the actual mutation, rather
                    // than closing archive handles between validation and apply.
                    error.IsVisible = false;
                    history.Do(command);
                    RefreshStatus();
                    return;
                }
                error.IsVisible = false;
                history.Do(command);
                RefreshStatus();
            }
            catch (Exception refused)
            {
                error.Text = refused.Message;
                error.IsVisible = true;
            }
            finally { committing = false; }
        }
        void AddButton(string name, string label, string action, bool enabled = true, string profile = ArchitecturalLightingAuthoring.BakeProfile)
        {
            var button = new Button { Name = name, Content = label, Margin = new Thickness(4, 0, 4, 4), IsEnabled = enabled };
            button.Click += (_, _) => Commit(action, profile);
            panel.Children.Add(button);
        }
        AddButton("ArchitectureBind", "Bind existing lighting", "bind", thing.ArchLightingIndex < 0 && level.ArchitecturalLighting.Count > 0);
        AddButton("ArchitectureReset", "Reset/rebind lighting to zero", "rebind");
        AddButton("ArchitectureBake", "Bake source lights (unoccluded)", "bake");
        AddButton("ArchitectureBakeOccluded", "Bake source lights (world shadows)", "bake", profile: ArchitecturalLightingAuthoring.WorldOccludedBakeProfile);
        AddButton("ArchitectureUnbind", "Unbind baked lighting", "unbind", thing.ArchLightingIndex >= 0);
        panel.Children.Add(error);
    }
}

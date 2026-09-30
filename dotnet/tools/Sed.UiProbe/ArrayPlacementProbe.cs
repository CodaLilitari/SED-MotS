using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sed.App;
using Sed.Core.Editing;
using Sed.Core.Model;

internal static class ArrayPlacementProbe
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok) => report("placement arrays: " + name, ok, "");
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        level.Templates.Add("crate", new Template { Name = "crate" });
        var history = new EditHistory(); int changes = 0; history.Changed += () => changes++;
        var window = new ArrayPlacementWindow(level, history);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            T Control<T>(string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
            void Click(string name)
            {
                var button = Control<Button>(name); button.BringIntoView(); Dispatcher.UIThread.RunJobs(); button.Focus();
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
            }
            Click("ArrayPreview");
            Check("preview validates without changing level or history", level.Things.Count == 0 && changes == 0
                && Control<TextBlock>("ArrayStatus").Text?.StartsWith("Valid: 3") == true);
            Click("ArrayCreate");
            Check("human create uses one undo step", level.Things.Count == 3 && changes == 1 && history.CanUndo);
            Check("same deterministic names as agent", level.Things.Select(thing => thing.Name).SequenceEqual(["array_000", "array_001", "array_002"]));
            Click("ArrayCreate");
            Check("duplicate name attempt leaves original batch intact", level.Things.Count == 3 && changes == 1
                && Control<TextBlock>("ArrayStatus").Text?.Contains("already exists") == true);
            history.Undo(); Check("one undo removes the entire batch", level.Things.Count == 0 && !history.CanUndo);
            Control<TextBox>("ArrayStep").Text = "1, 0, 0";
            Click("ArrayCreate");
            Check("boundary/outside array has no partial create", level.Things.Count == 0 && changes == 2
                && !string.IsNullOrEmpty(Control<TextBlock>("ArrayStatus").Text));

            Control<ComboBox>("ArrayType").SelectedIndex = 1;
            Control<ComboBox>("ArrayLayout").SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            var inherit = Control<CheckBox>("ArrayInheritRgb"); inherit.BringIntoView(); Dispatcher.UIThread.RunJobs(); inherit.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Check("human unlink enables independent RGB field", inherit.IsChecked == false && Control<TextBox>("ArrayRgbIntensity").IsEnabled);
            Control<TextBox>("ArrayRgbIntensity").Text = "0";
            Control<TextBox>("ArrayRgb").Text = "0.25, 0.5, 0.75";
            Control<TextBox>("ArrayLegacyRange").Text = "99";
            Control<TextBox>("ArrayCount").Text = "4";
            Click("ArrayCreate");
            Check("radial source lights keep explicit RGB zero", level.Lights.Count == 4 && level.Lights.All(light => light.RgbIntensity == 0
                && light.Intensity == 1 && light.LegacyRgbRange == 99) && changes == 3);
            history.Undo(); Check("light array undo removes the full batch", level.Lights.Count == 0 && !history.CanUndo);
        }
        catch (Exception error) { report("placement arrays: human dialog flow", false, error.ToString()); }
        finally { window.Close(); }
    }
}

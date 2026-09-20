using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sed.App;
using Sed.Core.Editing;
using Sed.Core.Model;

/// <summary>Actual sector inspector input on Avalonia's headless platform; no native windows.</summary>
internal static class SectorColormapProbe
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string label, bool passed, string detail = "") => report("sector colormap: " + label, passed, detail);
        foreach (bool enter in new[] { true, false })
        {
            string path = enter ? "Enter" : "focus loss";
            try
            {
                using var ui = new Fixture();
                ui.BeginEdit("duplicate.cmp");
                Exception? escaped = null;
                try
                {
                    if (enter) ui.Enter();
                    else ui.Blur();
                }
                catch (Exception error) { escaped = error; }
                Check(path + " contains ambiguous-name refusal inside the inspector", escaped is null, escaped?.ToString() ?? "");
                Check(path + " leaves the original name and numeric slot unchanged", ui.Sector.ColorMap == "other.cmp" && ui.Sector.ColorMapIndex == 2);
                Check(path + " refuses without adding undo or history events", !ui.History.CanUndo && ui.Changes == 0);
                Check(path + " displays an actionable ambiguity error", ui.HasVisibleError("ambiguous"));
            }
            catch (Exception error) { Check(path + " probe completed", false, error.ToString()); }
        }

        try
        {
            using var ui = new Fixture(selectedSlot: 1);
            var slots = ui.Slots();
            Check("registered entries expose distinct numeric labels", slots.Items.Cast<object>().Select(item => item.ToString())
                .SequenceEqual(new[] { "0: duplicate.cmp", "1: duplicate.cmp", "2: other.cmp" }));
            Check("opening duplicate slot 1 creates no edit", slots.SelectedIndex == 1 && !ui.History.CanUndo && ui.Changes == 0);
            ui.SelectPreviousSlot();
            Check("keyboard selection changes duplicate slot 1 to 0", ui.Sector.ColorMapIndex == 0 && ui.Sector.ColorMap == "duplicate.cmp" && ui.Slots().SelectedIndex == 0);
            ui.Blur();
            Check("slot selection and reentrant replacement create exactly one edit", ui.Changes == 1 && ui.History.CanUndo);
            ui.History.Undo(); ui.Drain();
            Check("one undo restores duplicate slot 1", ui.Sector.ColorMapIndex == 1 && ui.Slots().SelectedIndex == 1 && !ui.History.CanUndo && ui.History.CanRedo);
            ui.History.Redo(); ui.Drain();
            Check("redo restores duplicate slot 0", ui.Sector.ColorMapIndex == 0 && ui.Slots().SelectedIndex == 0);
            int changes = ui.Changes;
            ui.FocusName(); ui.Enter(); ui.Blur();
            Check("unchanged name Enter and blur retain numeric selection without another edit", ui.Sector.ColorMapIndex == 0 && ui.Changes == changes);
        }
        catch (Exception error) { Check("numeric selection probe completed", false, error.ToString()); }

        try
        {
            using var ui = new Fixture();
            ui.BeginEdit("custom.cmp"); ui.Enter(); ui.Blur();
            Check("name-only edit remains available and commits once through Enter plus blur", ui.Sector.ColorMap == "custom.cmp" && ui.Sector.ColorMapIndex == -1 && ui.Changes == 1);
            ui.FocusName();
            ui.History.Undo(); ui.Drain();
            Check("undo replacing a focused name field restores its original name and slot", ui.Sector.ColorMap == "other.cmp" && ui.Sector.ColorMapIndex == 2 && !ui.History.CanUndo && ui.Changes == 2);
            ui.History.Redo(); ui.Drain();
            Check("redo restores name-only state without a duplicate blur edit", ui.Sector.ColorMap == "custom.cmp" && ui.Sector.ColorMapIndex == -1 && ui.Changes == 3);
        }
        catch (Exception error) { Check("name edit probe completed", false, error.ToString()); }

        try
        {
            using var ui = new Fixture();
            ui.BeginEdit("duplicate.cmp"); ui.Enter();
            Check("ambiguous text remains available for correction", ui.HasVisibleError("ambiguous") && ui.Sector.ColorMapIndex == 2);
            ui.SelectPreviousSlot(); ui.Blur();
            Check("explicit slot selection repairs an ambiguous text request", ui.Sector.ColorMapIndex == 1 && ui.Sector.ColorMap == "duplicate.cmp" && ui.Changes == 1 && !ui.HasVisibleError("ambiguous"));
        }
        catch (Exception error) { Check("refusal recovery probe completed", false, error.ToString()); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Window _window;
        private readonly InspectorPanel _inspector;
        private readonly Button _focusSink = new() { Content = "Leave inspector" };
        public Sector Sector { get; }
        public EditHistory History { get; } = new();
        public int Changes { get; private set; }

        public Fixture(int selectedSlot = 2)
        {
            var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
            level.ColorMaps.AddRange(["duplicate.cmp", "duplicate.cmp", "other.cmp"]);
            Sector = level.Sectors[0];
            Sector.ColorMap = level.ColorMaps[selectedSlot];
            Sector.ColorMapIndex = selectedSlot;
            _inspector = new InspectorPanel { Mode = EditMode.Sector, Width = 480, Context = new InspectorContext(Level: level) };
            _inspector.SetHistory(History);
            _inspector.SetTarget(Sector);
            var contents = new DockPanel();
            DockPanel.SetDock(_focusSink, Dock.Bottom);
            contents.Children.Add(_focusSink);
            contents.Children.Add(_inspector);
            _window = new Window { Width = 640, Height = 800, Content = contents };
            // Reproduce the real synchronous history -> inspector rebuild path.
            History.Changed += Rebuild;
            _window.Show();
            Drain();
        }

        private void Rebuild() { Changes++; _inspector.SetTarget(Sector); }
        public void Drain() => Dispatcher.UIThread.RunJobs();

        private TextBox Field()
        {
            Drain();
            var row = _inspector.GetVisualDescendants().OfType<StackPanel>().Single(panel =>
                panel.Children.Count == 2 && panel.Children[0] is TextBlock label && label.Text == "Colormap" && panel.Children[1] is TextBox);
            return (TextBox)row.Children[1];
        }

        public bool HasVisibleError(string text) => _inspector.GetVisualDescendants().OfType<TextBlock>()
            .Any(label => label.IsVisible && label.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);

        public ComboBox Slots()
        {
            Drain();
            return _inspector.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "SectorColormapSlots");
        }

        public void SelectPreviousSlot()
        {
            Focus(Slots());
            _window.KeyPressQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            _window.KeyReleaseQwerty(PhysicalKey.ArrowUp, RawInputModifiers.None);
            Drain();
        }

        public void FocusName() => Focus(Field());

        private void Focus(InputElement control)
        {
            if (!control.Focus()) throw new InvalidOperationException("Headless sector control could not receive focus.");
            Drain();
            if (!control.IsFocused) throw new InvalidOperationException("Headless sector control lost focus before input.");
        }

        public void BeginEdit(string text)
        {
            var field = Field();
            Focus(field);
            field.SelectAll();
            _window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            _window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            _window.KeyTextInput(text);
            Drain();
            if (field.Text != text) throw new InvalidOperationException("Sector colormap text did not reach the focused editor.");
        }

        public void Enter()
        {
            _window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            _window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Drain();
        }

        public void Blur() => Focus(_focusSink);

        public void Dispose()
        {
            History.Changed -= Rebuild;
            // A failing baseline can still have invalid focused text. Remove
            // that pending input before teardown so later probe cases can run.
            Field().Text = Sector.ColorMap;
            _window.Close();
            Drain();
        }
    }
}

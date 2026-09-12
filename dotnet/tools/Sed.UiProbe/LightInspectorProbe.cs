using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sed.App;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

/// <summary>Real Avalonia focus and keyboard paths on the existing headless platform; no native windows.</summary>
internal static class LightInspectorProbe
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string label, bool passed, string detail = "") => report("light inspector: " + label, passed, detail);
        void Case(string label, Action run)
        {
            try { run(); }
            catch (Exception ex) { Check(label + " completed", false, ex.ToString()); }
        }

        Case("high-precision viewing", () =>
        {
            using var ui = new Fixture(PreciseLight());
            var before = State.Of(ui.Light);
            foreach (string label in new[] { "Flags", "Range", "White intensity", "RGB intensity", "Legacy range",
                         "Color R", "Color G", "Color B", "X", "Y", "Z", "Layer" })
            {
                ui.Focus(ui.Field(label));
                ui.Enter();
                ui.Blur();
            }
            Check("showing and leaving every source field preserves exact values", before == State.Of(ui.Light));
            Check("unchanged Enter and focus loss add no undo entries", !ui.History.CanUndo && ui.Changes == 0);
            Check("unchanged checks exercised real focus loss", ui.LostFocusEvents >= 12);
        });

        Case("independent edits and reentrant rebuild", () =>
        {
            using var ui = new Fixture(PreciseLight());
            var before = State.Of(ui.Light);
            ui.Edit("White intensity", "0.8765432109876543", enter: true);
            Check("Enter changes white alone at full precision", ui.Light.Intensity == .8765432109876543
                && ui.Light.RgbIntensity == before.Rgb && ui.Light.LegacyRgbRange == before.LegacyRange);
            Check("Enter plus synchronous inspector replacement is one edit", ui.Changes == 1 && ui.History.CanUndo,
                $"{ui.Changes} history events, expected 1");
            ui.History.Undo(); ui.Drain();
            Check("one undo fully restores the white edit", State.Of(ui.Light) == before && !ui.History.CanUndo);
            ui.History.Redo(); ui.Drain();
            Check("white redo restores the exact value", ui.Light.Intensity == .8765432109876543);

            int changes = ui.Changes;
            ui.Edit("RGB intensity", "0", enter: false);
            Check("focus loss authors explicit RGB zero without changing white", ui.Light.RgbIntensity == 0
                && ui.Light.Intensity == .8765432109876543);
            Check("focus-loss commit is one edit despite rebuilding", ui.Changes == changes + 1);
            ui.History.Undo(); ui.Drain();
            Check("RGB undo restores its independent source value", ui.Light.RgbIntensity == before.Rgb
                && ui.Light.Intensity == .8765432109876543);
            ui.History.Redo(); ui.Drain();
            Check("RGB redo retains explicit zero", ui.Light.RgbIntensity.HasValue && ui.Light.RgbIntensity == 0);
        });

        Case("linked intensity semantics", () =>
        {
            using var ui = new Fixture(new Light { Intensity = .5, Range = 4 });
            Check("inherited RGB starts linked and disabled", ui.Link().IsChecked == true && !ui.Field("RGB intensity").IsEnabled);
            ui.ToggleLink();
            Check("unlink authors an explicit value even when it equals white", ui.Light.RgbIntensity == .5
                && ui.Light.RgbIntensity.HasValue && ui.Field("RGB intensity").IsEnabled && ui.Changes == 1);
            ui.History.Undo(); ui.Drain();
            Check("unlink undo restores null inheritance", ui.Light.RgbIntensity is null && ui.Link().IsChecked == true);
            ui.History.Redo(); ui.Drain();
            Check("unlink redo restores explicit equal intensity", ui.Light.RgbIntensity == .5 && ui.Link().IsChecked == false);

            ui.Edit("White intensity", "0", enter: true);
            Check("unlinked RGB stays fixed when white changes", ui.Light.Intensity == 0 && ui.Light.RgbIntensity == .5);
            int changes = ui.Changes;
            ui.ToggleLink();
            Check("link replaces the override with null and follows zero", ui.Light.RgbIntensity is null
                && ui.Light.EffectiveRgbIntensity == 0 && !ui.Field("RGB intensity").IsEnabled && ui.Changes == changes + 1);
            ui.ToggleLink();
            Check("unlink at zero preserves explicit zero rather than null", ui.Light.RgbIntensity.HasValue && ui.Light.RgbIntensity == 0);
            ui.ToggleLink();
            ui.History.Undo(); ui.Drain();
            Check("link undo restores explicit zero and the editable field", ui.Light.RgbIntensity.HasValue
                && ui.Light.RgbIntensity == 0 && ui.Field("RGB intensity").IsEnabled);
            ui.History.Redo(); ui.Drain();
            Check("link redo restores inheritance", ui.Light.RgbIntensity is null && !ui.Field("RGB intensity").IsEnabled);

            ui.Edit("White intensity", "1.2345678901234567", enter: false);
            Check("linked RGB follows a later precise white edit without becoming explicit", ui.Light.RgbIntensity is null
                && ui.Light.EffectiveRgbIntensity == 1.2345678901234567
                && ui.Field("RGB intensity").Text == "1.2345678901234567");
        });

        Case("legacy metadata editing", () =>
        {
            using var ui = new Fixture(PreciseLight());
            var before = State.Of(ui.Light);
            ui.Edit("Legacy range", "", enter: true);
            Check("blank Enter removes metadata in one edit", ui.Light.LegacyRgbRange is null && ui.Changes == 1);
            Check("metadata edit leaves both intensities and primary range intact", ui.Light.Intensity == before.White
                && ui.Light.RgbIntensity == before.Rgb && ui.Light.Range == before.Range);
            ui.History.Undo(); ui.Drain();
            Check("metadata undo restores full original precision", State.Of(ui.Light) == before && !ui.History.CanUndo);
            ui.History.Redo(); ui.Drain();
            Check("metadata redo restores absence", ui.Light.LegacyRgbRange is null);
            int changes = ui.Changes;
            ui.Edit("Legacy range", "27.123456789012345", enter: false);
            Check("metadata focus loss retains full authored precision in one edit",
                ui.Light.LegacyRgbRange == 27.123456789012345 && ui.Changes == changes + 1 && ui.Light.Range == before.Range);
            changes = ui.Changes;
            ui.Focus(ui.Field("Legacy range")); ui.Enter(); ui.Blur();
            Check("revisiting authored metadata adds no duplicate edit", ui.Changes == changes);
        });

        Case("invalid numeric input", () =>
        {
            using var ui = new Fixture(PreciseLight());
            var before = State.Of(ui.Light);
            ui.Edit("White intensity", "NaN", enter: true);
            ui.Edit("RGB intensity", "Infinity", enter: false);
            ui.Edit("Legacy range", "invalid", enter: true);
            Check("invalid text cannot mutate light data or history", State.Of(ui.Light) == before && ui.Changes == 0 && !ui.History.CanUndo);
        });

        foreach (string label in new[] { "White intensity", "RGB intensity", "Legacy range" })
        {
            Case("pending " + label + " across selection", () =>
            {
                var first = PreciseLight();
                var second = new Light { Num = 1, Intensity = .25, RgbIntensity = .75, Range = 4, LegacyRgbRange = 6 };
                using var ui = new Fixture(first);
                var firstBefore = State.Of(first);
                var secondBefore = State.Of(second);
                const double pending = 2.9876543210987654;
                var firstAfter = label switch
                {
                    "White intensity" => firstBefore with { White = pending },
                    "RGB intensity" => firstBefore with { Rgb = pending },
                    _ => firstBefore with { LegacyRange = pending },
                };
                ui.BeginEdit(label, "2.9876543210987654");
                Check(label + " pending text has not committed", State.Of(first) == firstBefore && !ui.History.CanUndo);
                int lostFocus = ui.LostFocusEvents;
                ui.SelectLight(second);
                ui.Blur();
                Check(label + " selection change delivers real focus loss", ui.LostFocusEvents > lostFocus);
                Check(label + " pending value commits only to its original light", State.Of(first) == firstAfter
                    && State.Of(second) == secondBefore && ui.Changes == 1);
                Check(label + " history rebuild retains the newly selected light", ReferenceEquals(ui.Light, second)
                    && ui.Field("White intensity").Text == "0.25" && ui.Field("RGB intensity").Text == "0.75");
                ui.History.Undo(); ui.Drain();
                Check(label + " undo while another light is selected restores the original only", State.Of(first) == firstBefore
                    && State.Of(second) == secondBefore && ReferenceEquals(ui.Light, second) && !ui.History.CanUndo);
                ui.History.Redo(); ui.Drain();
                Check(label + " redo remains bound to the original light", State.Of(first) == firstAfter
                    && State.Of(second) == secondBefore && ReferenceEquals(ui.Light, second));
            });
        }

        Case("pending text across document replacement", () =>
        {
            var oldLight = PreciseLight();
            var newLight = new Light { Intensity = .625, RgbIntensity = 1.875, Range = 12, LegacyRgbRange = 24 };
            using var ui = new Fixture(oldLight);
            var newBefore = State.Of(newLight);
            ui.BeginEdit("RGB intensity", "0");
            int lostFocus = ui.LostFocusEvents;
            ui.ReplaceDocument(newLight);
            ui.Blur();
            Check("document replacement detaches the pending field through focus loss", ui.LostFocusEvents > lostFocus);
            Check("old pending text cannot alter the replacement document", State.Of(newLight) == newBefore
                && ReferenceEquals(ui.Light, newLight) && ui.Field("RGB intensity").Text == "1.875");
            Check("replacement history contains no edit bound to the old document", !ui.History.CanUndo && !ui.History.CanRedo);
            ui.History.Undo(); ui.Drain();
            Check("undo after replacement cannot resurrect or mutate old selection", State.Of(newLight) == newBefore
                && ReferenceEquals(ui.Light, newLight) && !ui.History.CanUndo);
        });
    }

    private static Light PreciseLight() => new()
    {
        Intensity = .12345678901234567, RgbIntensity = 1.2345678901234567, Range = 8.123456789012345,
        LegacyRgbRange = 19.123456789012344, Color = new ColorF(.12345679f, .45678912f, .78912345f),
        Position = new Vec3(1.1234567890123457, -2.2345678901234567, 3.345678901234567), Flags = 0x12345, Layer = 2,
    };

    private sealed record State(double White, double? Rgb, double Range, double? LegacyRange, ColorF Color, Vec3 Position, long Flags, int Layer)
    {
        public static State Of(Light light) => new(light.Intensity, light.RgbIntensity, light.Range, light.LegacyRgbRange,
            light.Color, light.Position, light.Flags, light.Layer);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Window _window;
        private readonly InspectorPanel _inspector;
        private readonly Button _focusSink = new() { Content = "Leave inspector" };
        private readonly HashSet<TextBox> _tracked = new();
        private readonly SelectionSet _selection = new();
        private Level _level;
        public Light Light => _selection.PrimaryLight ?? throw new InvalidOperationException("No light is selected.");
        public EditHistory History { get; } = new();
        public int Changes { get; private set; }
        public int LostFocusEvents { get; private set; }

        public Fixture(Light light)
        {
            _level = new Level { Kind = ProjectType.MysteriesOfTheSith };
            _level.Lights.Add(light);
            _selection.SelectOnly(light);
            _inspector = new InspectorPanel { Mode = EditMode.Light, Width = 480, Context = new InspectorContext(Level: _level) };
            _inspector.SetHistory(History);
            _inspector.SetTarget(light);
            var contents = new DockPanel();
            DockPanel.SetDock(_focusSink, Dock.Bottom);
            contents.Children.Add(_focusSink);
            contents.Children.Add(_inspector);
            _window = new Window { Width = 640, Height = 1000, Content = contents };
            // Match MainWindow's synchronous History.Changed -> UpdateInspectorTarget -> SetTarget path.
            History.Changed += Rebuild;
            _selection.Changed += RefreshTarget;
            _window.Show();
            Drain();
        }

        private void Rebuild()
        {
            Changes++;
            RefreshTarget();
        }

        private void RefreshTarget() => _inspector.SetTarget(_selection.PrimaryLight);

        public void SelectLight(Light light)
        {
            if (!_level.Lights.Contains(light)) _level.Lights.Add(light);
            _selection.SelectOnly(light);
            Drain();
        }

        public void ReplaceDocument(Light light)
        {
            // Mirror LoadLevel/SetLevel's inspector boundary: new context, clear selection,
            // then clear old history. This does not instantiate the Vulkan game viewport.
            _level = new Level { Kind = ProjectType.MysteriesOfTheSith };
            _level.Lights.Add(light);
            _inspector.Context = new InspectorContext(Level: _level);
            _selection.Clear();
            History.Clear();
            SelectLight(light);
        }

        public void Drain() => Dispatcher.UIThread.RunJobs();

        public TextBox Field(string label)
        {
            Drain();
            var row = _inspector.GetVisualDescendants().OfType<StackPanel>().Single(panel =>
                panel.Children.Count == 2 && panel.Children[0] is TextBlock text && text.Text == label && panel.Children[1] is TextBox);
            var field = (TextBox)row.Children[1];
            if (_tracked.Add(field)) field.LostFocus += (_, _) => LostFocusEvents++;
            return field;
        }

        public CheckBox Link()
        {
            Drain();
            return _inspector.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "RGB follows white"));
        }

        public void Focus(Avalonia.Input.InputElement control)
        {
            if (!control.Focus()) throw new InvalidOperationException("Headless test control could not receive focus.");
            Drain();
            if (!control.IsFocused) throw new InvalidOperationException("Headless test control lost focus before input.");
        }

        public void Enter()
        {
            _window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            _window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Drain();
        }

        public void Blur() => Focus(_focusSink);

        public void BeginEdit(string label, string text)
        {
            var field = Field(label);
            Focus(field);
            field.SelectAll();
            _window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            _window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            if (text.Length > 0) _window.KeyTextInput(text);
            Drain();
            if (field.Text != text) throw new InvalidOperationException($"Text input for '{label}' did not reach the focused editor.");
        }

        public void Edit(string label, string text, bool enter)
        {
            BeginEdit(label, text);
            if (enter) Enter();
            Blur();
        }

        public void ToggleLink()
        {
            Focus(Link());
            _window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            _window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Drain();
            Blur();
        }

        public void Dispose()
        {
            History.Changed -= Rebuild;
            _selection.Changed -= RefreshTarget;
            _window.Close();
            Drain();
        }
    }
}

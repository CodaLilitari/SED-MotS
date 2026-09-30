using System.Globalization;
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

/// <summary>Actual texture workbench control input using only synthetic geometry and headless windows.</summary>
internal static class TextureWorkbenchProbe
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string label, bool passed, string detail = "") => report("texture workbench: " + label, passed, detail);
        try
        {
            using var ui = new Fixture();
            Check("opening preserves both selected surfaces and history", ui.OriginalUvs() && ui.Changes == 0 && !ui.History.CanUndo);
            ui.Text("TexelsPerUnit", "0"); ui.Apply();
            Check("zero projection scale shows an inline error without editing either surface", ui.Error("positive") && ui.OriginalUvs() && ui.Changes == 0);
            ui.Text("TexelsPerUnit", "8"); ui.Text("OriginX", "1"); ui.Text("OriginY", "2"); ui.Apply();
            Check("projection button edits both surfaces in one transaction", ui.Changes == 1 && ui.History.CanUndo && ui.Surfaces.All(surface => surface.Corners.All(c => Near(c.Uv.U, (c.Vertex.Position.X - 1) * 8) && Near(c.Uv.V, (c.Vertex.Position.Y - 2) * 8))));
            Check("successful correction clears the inline error", !ui.Error("") && ui.Control<TextBlock>("Status").Text?.StartsWith("Updated 2 surfaces", StringComparison.Ordinal) == true);
            Check("projection preserves corner lighting and unselected UVs", ui.UnrelatedPreserved());
            ui.History.Undo(); ui.Drain();
            Check("single undo restores exact original UVs on both surfaces", ui.OriginalUvs() && !ui.History.CanUndo && ui.History.CanRedo);
            ui.History.Redo(); ui.Drain();
            Check("redo restores the projected pair", ui.Surfaces.All(surface => surface.Corners.All(c => Near(c.Uv.U, (c.Vertex.Position.X - 1) * 8) && Near(c.Uv.V, (c.Vertex.Position.Y - 2) * 8))));
        }
        catch (Exception error) { Check("projection control flow completed", false, error.ToString()); }

        try
        {
            using var ui = new Fixture();
            ui.Control<ComboBox>("Mode").SelectedIndex = 1; ui.Drain();
            ui.Text("PivotU", "2"); ui.Text("PivotV", "3"); ui.Text("ScaleU", "2"); ui.Text("ScaleV", "-1");
            ui.Text("Rotation", "90"); ui.Text("OffsetU", "5"); ui.Text("OffsetV", "7"); ui.Apply();
            Check("transform button uses one shared pivot, mirrored scale, CCW rotation and final offset", ui.Changes == 1 && ui.Surfaces.SelectMany(surface => surface.Corners).Select((corner, index) =>
                Near(corner.Uv.U, ui.Before[index].V + 4) && Near(corner.Uv.V, 2 * ui.Before[index].U + 6)).All(value => value));
            Check("transform retains lighting and unselected faces", ui.UnrelatedPreserved());
            var transformed = ui.Surfaces.SelectMany(surface => surface.Corners).Select(c => c.Uv).ToArray();
            ui.Text("ScaleV", "0"); ui.Apply();
            Check("invalid transform leaves earlier edit and all selected UVs unchanged", ui.Error("nonzero") && ui.Changes == 1 && transformed.SequenceEqual(ui.Surfaces.SelectMany(surface => surface.Corners).Select(c => c.Uv)));
            ui.History.Undo(); ui.Drain();
            Check("failed edit added no history entry and one undo restores both originals", ui.OriginalUvs() && !ui.History.CanUndo);
        }
        catch (Exception error) { Check("transform control flow completed", false, error.ToString()); }

        try
        {
            using var ui = new Fixture(perpendicularSecondFace: true);
            ui.Text("TexelsPerUnit", "8"); ui.Apply();
            Check("late edge-on face refuses without a partial first-face edit", ui.Error("edge-on") && ui.OriginalUvs() && !ui.History.CanUndo && ui.Changes == 0);
        }
        catch (Exception error) { Check("atomic refusal control flow completed", false, error.ToString()); }

        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            using var ui = new Fixture();
            ui.Text("TexelsPerUnit", "1,5"); ui.Apply();
            Check("localized comma input is explicitly refused rather than misread", ui.Error("decimal point") && ui.OriginalUvs() && ui.Changes == 0);
            ui.Text("TexelsPerUnit", "1.5"); ui.Apply();
            Check("decimal-point values work independently of current culture", ui.Changes == 1 && ui.Surfaces.All(surface => surface.Corners.All(c => Near(c.Uv.U, c.Vertex.Position.X * 1.5) && Near(c.Uv.V, c.Vertex.Position.Y * 1.5))));
        }
        catch (Exception error) { Check("numeric parsing control flow completed", false, error.ToString()); }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    private static bool Near(double actual, double expected) => System.Math.Abs(actual - expected) < 1e-9;
    private sealed class Fixture : IDisposable
    {
        private readonly TextureWorkbenchWindow _window;
        private readonly Surface _unselected;
        private readonly TexVertex[] _unselectedBefore;
        public Surface[] Surfaces { get; }
        public TexVertex[] Before { get; }
        public EditHistory History { get; } = new();
        public int Changes { get; private set; }

        public Fixture(bool perpendicularSecondFace = false)
        {
            var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
            var second = SectorFactory.CreateBox(level, new Vec3(2, 0, 0), 1, string.Empty, -1); level.Sectors.Add(second);
            Surfaces = [level.Sectors[0].Surfaces[0], second.Surfaces[perpendicularSecondFace ? 2 : 0]];
            foreach (var corner in Surfaces.SelectMany(surface => surface.Corners))
            { corner.MonoIntensity = .125f; corner.Intensity = new ColorF(.25f, .5f, .75f); }
            Before = Surfaces.SelectMany(surface => surface.Corners).Select(c => c.Uv).ToArray();
            _unselected = level.Sectors[0].Surfaces[1]; _unselectedBefore = _unselected.Corners.Select(c => c.Uv).ToArray();
            History.Changed += Changed;
            _window = new TextureWorkbenchWindow(Surfaces, History);
            _window.Show(); Drain();
        }

        private void Changed() => Changes++;
        public void Drain() => Dispatcher.UIThread.RunJobs();
        public T Control<T>(string suffix) where T : Control => _window.GetVisualDescendants().OfType<T>().Single(c => c.Name == "TextureWorkbench" + suffix);
        public bool OriginalUvs() => Before.SequenceEqual(Surfaces.SelectMany(surface => surface.Corners).Select(c => c.Uv));
        public bool UnrelatedPreserved() => _unselectedBefore.SequenceEqual(_unselected.Corners.Select(c => c.Uv)) && Surfaces.SelectMany(surface => surface.Corners).All(c => c.MonoIntensity == .125f && c.Intensity == new ColorF(.25f, .5f, .75f));
        public bool Error(string text)
        {
            var error = Control<TextBlock>("Error");
            return error.IsVisible && error.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
        }

        public void Text(string suffix, string value)
        {
            var field = Control<TextBox>(suffix);
            if (!field.Focus()) throw new InvalidOperationException($"Could not focus texture workbench field {suffix}.");
            field.SelectAll();
            _window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None); _window.KeyReleaseQwerty(PhysicalKey.Backspace, RawInputModifiers.None);
            _window.KeyTextInput(value); Drain();
            if (field.Text != value) throw new InvalidOperationException($"Text did not reach texture workbench field {suffix}.");
        }

        public void Apply()
        {
            var button = Control<Button>("Apply");
            if (!button.Focus()) throw new InvalidOperationException("Could not focus texture workbench Apply button.");
            _window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None); _window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Drain();
        }

        public void Dispose() { History.Changed -= Changed; _window.Close(); Drain(); }
    }
}

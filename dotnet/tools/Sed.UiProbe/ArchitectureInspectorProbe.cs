using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sed.App;
using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Gob;

/// <summary>Real headless button/selection input with original on-disk model resources.</summary>
internal static class ArchitectureInspectorProbe
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool pass, string detail = "") => report("architecture inspector: " + name, pass, detail);
        try
        {
            using var ui = new Fixture();
            Check("opening the inspector leaves binding and history unchanged", ui.Thing.ArchLightingIndex == -1 && !ui.History.CanUndo && ui.Status("unbound"));
            Check("bake label states the unoccluded policy", (string?)ui.Button("ArchitectureBake").Content == "Bake source lights (unoccluded)");
            ui.Press("ArchitectureReset");
            Check("reset bootstraps an unbound thing with zero samples", ui.Thing.ArchLightingIndex == 2 && ui.Sample() == default && ui.Status("reset") && ui.Changes == 1);
            var resetObject = ui.Level.ArchitecturalLighting[2];
            var resetProvenance = ui.Thing.ArchLightingProvenance;
            ui.History.Undo(); ui.Drain();
            Check("one undo removes only the appended reset object", ui.Thing.ArchLightingIndex == -1 && ui.Level.ArchitecturalLighting.Count == 2 && ui.Status("unbound") && !ui.History.CanUndo);
            ui.History.Redo(); ui.Drain();
            Check("redo restores reset samples and provenance", ui.Thing.ArchLightingIndex == 2 && ui.Sample() == default && ui.Thing.ArchLightingProvenance == resetProvenance);
            ui.Press("ArchitectureBake");
            Check("bake computes independent channels at half range", Near(ui.Sample().Mono, .2) && Near(ui.Sample().Red, .1) && Near(ui.Sample().Green, .05) && Near(ui.Sample().Blue, .025));
            Check("bake reports current document inputs without claiming resource freshness", ui.Status("current-inputs") && ui.Status("resources are checked"));
            Check("bake retains the previous archived samples", ui.Level.ArchitecturalLighting.Count == 4 && resetObject.Meshes[0].Vertices.All(sample => sample == default));
            var baked = ui.Sample(); var provenance = ui.Thing.ArchLightingProvenance;
            ui.History.Undo(); ui.Drain();
            Check("one undo restores reset state", ui.Thing.ArchLightingIndex == 2 && ui.Status("reset") && ui.Sample() == default);
            ui.History.Redo(); ui.Drain();
            Check("redo restores the exact baked samples and provenance", ui.Sample() == baked && ui.Thing.ArchLightingProvenance == provenance && ui.Status("current-inputs"));
            ui.History.Do(new SetThingPositionCommand(ui.Thing, new Vec3(1, 0, 0))); ui.Drain();
            Check("pose edit exposes stale status while preserving samples", ui.Status("stale") && ui.Sample() == baked);
            ui.History.Undo(); ui.Drain();
            Check("undo of the pose restores current-inputs status", ui.Status("current-inputs"));
            ui.Press("ArchitectureUnbind");
            Check("unbind clears attachment and provenance but retains arrays", ui.Thing.ArchLightingIndex == -1 && ui.Thing.ArchLightingProvenance is null && ui.Level.ArchitecturalLighting.Count == 4);
            ui.History.Undo(); ui.Drain();
            Check("undo of unbind restores baked provenance and samples", ui.Thing.ArchLightingProvenance == provenance && ui.Sample() == baked && ui.Status("current-inputs"));
        }
        catch (Exception error) { Check("reset/bake/undo flow completed", false, error.ToString()); }

        try
        {
            using var ui = new Fixture();
            ui.NextSlot(); ui.Press("ArchitectureBind");
            Check("keyboard selection binds exactly existing object one", ui.Thing.ArchLightingIndex == 2 && ui.Sample() == new ArchitecturalLightVertex(.125, .25, .5, .75) && ui.Status("bound"));
            Check("binding clones samples rather than sharing the archived object", !ReferenceEquals(ui.Level.ArchitecturalLighting[1], ui.Level.ArchitecturalLighting[2]) &&
                !ReferenceEquals(ui.Level.ArchitecturalLighting[1].Meshes[0], ui.Level.ArchitecturalLighting[2].Meshes[0]));
            Check("binding does not masquerade as a bake", ui.Thing.ArchLightingProvenance?.Origin == "bound" && ui.Thing.ArchLightingProvenance.InputSha256 == "");
            Check("button activation plus synchronous inspector replacement adds one edit", ui.Changes == 1);
            ui.History.Undo(); ui.Drain();
            Check("bind undo restores unbound and retains both archived objects", ui.Thing.ArchLightingIndex == -1 && ui.Level.ArchitecturalLighting.Count == 2 && ui.Status("unbound"));
        }
        catch (Exception error) { Check("existing-slot binding flow completed", false, error.ToString()); }

        try
        {
            using var ui = new Fixture(resources: false);
            ui.Press("ArchitectureBake", expectSuccess: false);
            Check("missing configured resources produce a visible actionable error", ui.Error("Configure") && ui.Thing.ArchLightingIndex == -1 && !ui.History.CanUndo && ui.Changes == 0, ui.VisibleError());
            ui.EnableResources();
            ui.Press("ArchitectureBake");
            Check("resource refusal can be corrected and retried", ui.Status("current-inputs") && !ui.Error("Configure") && ui.Changes == 1 && Near(ui.Sample().Mono, .2));
        }
        catch (Exception error) { Check("resource refusal recovery completed", false, error.ToString()); }

        try
        {
            using var ui = new Fixture(pivot: true);
            ui.Press("ArchitectureBake", expectSuccess: false);
            Check("unsupported pivot refusal stays inside the control", ui.Error("pivot") && ui.Thing.ArchLightingIndex == -1 && ui.Level.ArchitecturalLighting.Count == 2 && !ui.History.CanUndo && ui.Changes == 0, ui.VisibleError());
        }
        catch (Exception error) { Check("unsupported-model refusal completed", false, error.ToString()); }
    }

    private static bool Near(double actual, double expected) => double.IsFinite(actual) && Math.Abs(actual - expected) < 1e-12;

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly GobArchive _archive;
        private readonly InspectorPanel _inspector;
        private readonly Window _window;
        private bool _resources;
        public Level Level { get; } = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        public Thing Thing { get; }
        public EditHistory History { get; } = new();
        public int Changes { get; private set; }

        public Fixture(bool resources = true, bool pivot = false)
        {
            _resources = resources;
            _directory = Path.Combine(Path.GetTempPath(), "sed-arch-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            string archivePath = Path.Combine(_directory, "original.goo");
            string model = pivot ? Model.Replace("0 0 0 root", "1 0 0 root", StringComparison.Ordinal) : Model;
            GobWriter.Build(archivePath, new[] { ("3do/fixture.3do", Encoding.ASCII.GetBytes(model)) });
            _archive = GobArchive.Open(archivePath);
            Thing = Level.NewThing(); Thing.Values["model3d"] = "fixture.3do"; Thing.Values["thingflags"] = "0x8";
            Thing.Sector = Level.Sectors[0];
            Level.Lights.Add(new Light { Position = Vec3.Zero, Intensity = .8, RgbIntensity = .4, Color = new ColorF(1, .5f, .25f), Range = 4 });
            foreach (var sample in new[] { new ArchitecturalLightVertex(.91, .81, .71, .61), new ArchitecturalLightVertex(.125, .25, .5, .75) })
            {
                var obj = new ArchitecturalLightObject(); var mesh = new ArchitecturalLightMesh();
                mesh.Vertices.AddRange(new[] { sample, sample, sample }); obj.Meshes.Add(mesh); Level.ArchitecturalLighting.Add(obj);
            }
            _inspector = new InspectorPanel { Mode = EditMode.Thing, Width = 580,
                Context = new InspectorContext(Level: Level,
                    CaptureArchitectureResources: () => _resources ? GuiResourceExport.CaptureResources(new[] { _archive }) : null) };
            _inspector.SetHistory(History); _inspector.SetTarget(Thing);
            _window = new Window { Width = 640, Height = 1100, Content = _inspector };
            History.Changed += Rebuild;
            _window.Show(); Drain();
        }
        private void Rebuild() { Changes++; _inspector.SetTarget(Thing); }
        public void Drain() => Dispatcher.UIThread.RunJobs();
        public void EnableResources() => _resources = true;
        public Button Button(string name) => _inspector.GetVisualDescendants().OfType<Button>().Single(control => control.Name == name);
        public bool Status(string text) => _inspector.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Name == "ArchitectureStatus" && control.Text?.Contains(text, StringComparison.Ordinal) == true);
        public bool Error(string text) => _inspector.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Name == "ArchitectureError" && control.IsVisible && control.Text?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
        public string VisibleError() => string.Join("; ", _inspector.GetVisualDescendants().OfType<TextBlock>()
            .Where(control => control.Name == "ArchitectureError" && control.IsVisible).Select(control => control.Text));
        public ArchitecturalLightVertex Sample() => Level.ArchitecturalLighting[Thing.ArchLightingIndex].Meshes[0].Vertices[0];
        public void Press(string name, bool expectSuccess = true)
        {
            var button = Button(name);
            if (!button.IsEnabled || !button.Focus()) throw new InvalidOperationException("Architecture button was not enabled and focusable: " + name);
            Drain();
            _window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            _window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Drain();
            if (expectSuccess && VisibleError() is { Length: > 0 } refusal)
                throw new InvalidOperationException(name + " was refused visibly: " + refusal);
        }
        public void NextSlot()
        {
            var slots = _inspector.GetVisualDescendants().OfType<ComboBox>().Single(control => control.Name == "ArchitectureSlots");
            if (!slots.Focus()) throw new InvalidOperationException("Architecture slots could not receive focus.");
            _window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            _window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
            Drain();
            if (slots.SelectedIndex != 1) throw new InvalidOperationException("Keyboard input did not select object one.");
        }
        public void Dispose()
        {
            History.Changed -= Rebuild;
            _window.Close(); Drain(); _archive.Dispose();
            File.Delete(Path.Combine(_directory, "original.goo"));
            Directory.Delete(_directory);
        }
    }

    // An original triangle at x=2, with no pivots, no LOD aliases and no transforms.
    private const string Model = """
        SECTION: HEADER
        3DO 2.2
        END
        SECTION: MODELRESOURCE
        MATERIALS 1
        0: fixture.mat
        END
        SECTION: GEOMETRYDEF
        RADIUS 4
        INSERT OFFSET 0 0 0
        GEOSETS 1
        GEOSET 0
        MESHES 1
        MESH 0
        NAME original
        RADIUS 4
        GEOMETRYMODE 4
        LIGHTINGMODE 3
        TEXTUREMODE 3
        VERTICES 3
        0: 2 0 0 0
        1: 2 1 0 0
        2: 2 0 1 0
        TEXTURE VERTICES 3
        0: 0 0
        1: 1 0
        2: 0 1
        VERTEX NORMALS
        0: 1 0 0
        1: 1 0 0
        2: 1 0 0
        FACES 1
        0: 0 0x0 4 3 3 0 3 0,0 1,1 2,2
        FACE NORMALS
        0: 1 0 0
        END
        SECTION: HIERARCHYDEF
        HIERARCHY NODES 1
        0: 0x0 0x1 0 -1 -1 -1 0 0 0 0 0 0 0 0 0 0 root
        END
        """;
}

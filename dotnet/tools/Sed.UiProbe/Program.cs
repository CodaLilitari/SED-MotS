using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Sed.App;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Query;

// Drives MapView's real pointer-input path headlessly: rubber-band box select,
// Ctrl+click toggling, and pan-vs-select modifier routing. These paths live in a
// view and cannot be reached from Sed.Core unit tests, so they are exercised here
// with simulated mouse input against a synthetic level.

var failures = new List<string>();

void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
    if (!ok) failures.Add(label);
}

// A level of 9 boxes on a 3×3 grid in XY, 4 units apart, each half-extent 1.
// In the Top (XY) view they are well separated, so a rectangle can enclose a
// predictable subset.
static Level MakeGridLevel()
{
    var level = new Level();
    for (int gy = 0; gy < 3; gy++)
        for (int gx = 0; gx < 3; gx++)
        {
            var sector = SectorFactory.CreateBox(level, new Vec3(gx * 4, gy * 4, 0), 1.0, "dflt.mat", 0);
            level.Sectors.Add(sector);
        }
    level.RenumberSectors();

    for (int i = 0; i < 9; i++)
        level.Things.Add(new Thing
        {
            Name = $"thing{i}",
            Sector = level.Sectors[i],
            Position = new Vec3((i % 3) * 4, (i / 3) * 4, 0),
        });
    level.RenumberThings();

    // One light per box, offset so it never coincides with a thing marker.
    for (int i = 0; i < 9; i++)
        level.Lights.Add(new Light
        {
            Position = new Vec3((i % 3) * 4 + 0.5, (i / 3) * 4 + 0.5, 0),
            Range = 4,
            Intensity = 1,
            Color = Sed.Core.Math.ColorF.White,
        });
    level.RenumberLights();
    return level;
}

var session = HeadlessUnitTestSession.StartNew(typeof(AppEntry));
await session.Dispatch(() =>
{
    var level = MakeGridLevel();
    var selection = new SelectionSet();
    var map = new MapView { Selection = selection, History = new EditHistory() };

    var window = new Window { Width = 800, Height = 600, Content = map };
    window.Show();
    map.SetLevel(level);
    Dispatcher.UIThread.RunJobs();

    // Force a deterministic view transform instead of relying on auto-framing.
    map.Measure(new Size(800, 600));
    map.Arrange(new Rect(0, 0, 800, 600));
    Dispatcher.UIThread.RunJobs();

    Point ScreenOf(Vec3 world) => map.WorldToScreen(world);

    // ---- 1. Box select in Thing mode ----
    map.Mode = EditMode.Thing;

    // Rectangle around the bottom-left 2×2 block of things (0,0)..(4,4).
    var a = ScreenOf(new Vec3(-1.5, -1.5, 0));
    var b = ScreenOf(new Vec3(5.5, 5.5, 0));
    var topLeft = new Point(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y));
    var bottomRight = new Point(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y));

    window.MouseDown(topLeft, MouseButton.Left);
    window.MouseMove(new Point((topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2));
    window.MouseMove(bottomRight);
    window.MouseUp(bottomRight, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();

    Check("box select grabs the enclosed things", selection.Things.Count == 4,
        $"{selection.Things.Count} thing(s), expected 4");
    Check("box select ignores things outside the box",
        selection.Things.All(t => t.Position.X < 5 && t.Position.Y < 5));

    // ---- 2. Ctrl+drag extends an existing box selection ----
    var c = ScreenOf(new Vec3(6.5, -1.5, 0));
    var d = ScreenOf(new Vec3(9.5, 5.5, 0));
    var tl2 = new Point(System.Math.Min(c.X, d.X), System.Math.Min(c.Y, d.Y));
    var br2 = new Point(System.Math.Max(c.X, d.X), System.Math.Max(c.Y, d.Y));

    window.MouseDown(tl2, MouseButton.Left, RawInputModifiers.Control);
    window.MouseMove(br2, RawInputModifiers.Control);
    window.MouseUp(br2, MouseButton.Left, RawInputModifiers.Control);
    Dispatcher.UIThread.RunJobs();

    Check("ctrl+box select adds to the selection", selection.Things.Count == 6,
        $"{selection.Things.Count} thing(s), expected 4 + 2");

    // ---- 3. Plain click on empty space clears ----
    // (2,2) is a gap between boxes and, unlike a point far outside the level,
    // still projects inside the control — a click outside the control bounds
    // would never reach the view at all.
    var empty = ScreenOf(new Vec3(2, 2, 0));
    window.MouseDown(empty, MouseButton.Left);
    window.MouseUp(empty, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();

    Check("plain click on empty space clears the selection", selection.IsEmpty,
        $"{selection.Count} still selected");

    // ---- 4. Box select in Surface mode requires full containment ----
    map.Mode = EditMode.Surface;
    selection.Clear();

    // A box tightly around one sector encloses all of its surfaces...
    var e1 = ScreenOf(new Vec3(-1.5, -1.5, 0));
    var e2 = ScreenOf(new Vec3(1.5, 1.5, 0));
    var tl3 = new Point(System.Math.Min(e1.X, e2.X), System.Math.Min(e1.Y, e2.Y));
    var br3 = new Point(System.Math.Max(e1.X, e2.X), System.Math.Max(e1.Y, e2.Y));

    window.MouseDown(tl3, MouseButton.Left);
    window.MouseMove(br3);
    window.MouseUp(br3, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();

    Check("box select in surface mode selects the enclosed sector's surfaces",
        selection.Surfaces.Count == 6, $"{selection.Surfaces.Count} surface(s), expected 6");
    Check("all selected surfaces belong to the enclosed sector",
        selection.Surfaces.All(s => s.Sector == level.Sectors[0]));

    // ...but a box covering only the west half must not pick up surfaces that
    // extend past it. In a top-down view the west wall collapses to the line
    // x = -1 and is legitimately enclosed; the floor spans x -1..1 and is not.
    selection.Clear();
    var h1 = ScreenOf(new Vec3(-1.5, -1.5, 0));
    var h2 = ScreenOf(new Vec3(0.0, 1.5, 0));
    var tl4 = new Point(System.Math.Min(h1.X, h2.X), System.Math.Min(h1.Y, h2.Y));
    var br4 = new Point(System.Math.Max(h1.X, h2.X), System.Math.Max(h1.Y, h2.Y));

    window.MouseDown(tl4, MouseButton.Left);
    window.MouseMove(br4);
    window.MouseUp(br4, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();

    var floor = level.Sectors[0].Surfaces[0];      // {0,1,2,3}, spans the full footprint
    Check("a partial box excludes surfaces extending beyond it",
        !selection.Surfaces.Contains(floor) && selection.Surfaces.Count < 6,
        $"{selection.Surfaces.Count} surface(s), floor excluded={!selection.Surfaces.Contains(floor)}");

    // ---- 5. Lights are pickable and box-selectable in Light mode ----
    map.Mode = EditMode.Light;
    selection.Clear();

    var lightPoint = ScreenOf(new Vec3(0.5, 0.5, 0));
    window.MouseDown(lightPoint, MouseButton.Left);
    window.MouseUp(lightPoint, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();

    Check("clicking a light selects it", selection.Lights.Count == 1,
        $"{selection.Lights.Count} light(s)");

    // A box over the bottom-left 2×2 block should take four lights and no things.
    selection.Clear();
    var l1 = ScreenOf(new Vec3(-1.5, -1.5, 0));
    var l2 = ScreenOf(new Vec3(5.5, 5.5, 0));
    var ltl = new Point(System.Math.Min(l1.X, l2.X), System.Math.Min(l1.Y, l2.Y));
    var lbr = new Point(System.Math.Max(l1.X, l2.X), System.Math.Max(l1.Y, l2.Y));

    window.MouseDown(ltl, MouseButton.Left);
    window.MouseMove(lbr);
    window.MouseUp(lbr, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();

    Check("box select in light mode takes only lights",
        selection.Lights.Count == 4 && selection.Things.Count == 0,
        $"{selection.Lights.Count} light(s), {selection.Things.Count} thing(s)");

    // Light mode must not pick things, and thing mode must not pick lights.
    selection.Clear();
    map.Mode = EditMode.Thing;
    window.MouseDown(lightPoint, MouseButton.Left);
    window.MouseUp(lightPoint, MouseButton.Left);
    Dispatcher.UIThread.RunJobs();
    Check("thing mode ignores lights", selection.Lights.Count == 0,
        $"{selection.Lights.Count} light(s) picked in thing mode");

    // ---- 6b. Header editor renders and commits through the history ----
    {
        var history = new EditHistory();
        var header = new HeaderEditorWindow(level.Header, history);
        header.Show();
        Dispatcher.UIThread.RunJobs();

        Check("header editor renders", header.CaptureRenderedFrame() is not null);

        float before = level.Header.Gravity;
        history.Do(HeaderField.Set(level.Header, "gravity", before + 5f,
            x => x.Gravity, (x, v) => x.Gravity = v));
        header.Rebuild();
        Dispatcher.UIThread.RunJobs();

        Check("header edit lands on the model", System.Math.Abs(level.Header.Gravity - (before + 5f)) < 1e-6);
        history.Undo();
        Check("header edit undoes", System.Math.Abs(level.Header.Gravity - before) < 1e-6);

        header.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ---- 5b. Picker dialog filters and returns a value ----
    {
        var items = new List<PickerItem>
        {
            new("00wall.mat"), new("01wgril1.mat"), new("01wlite2.mat"), new("02floor.mat"),
        };
        var picker = new PickerDialog("Material", items, "01wgril1.mat");
        picker.Show();
        Dispatcher.UIThread.RunJobs();

        Check("picker dialog renders", picker.CaptureRenderedFrame() is not null);
        picker.Close();
        Dispatcher.UIThread.RunJobs();

        // Level-object pickers label entries the same way Find does.
        var things = PickerField.LevelObjects(level, FindKind.Thing);
        Check("thing picker lists every thing with a label",
            things.Count == level.Things.Count && things.All(i => i.Label.Length > 0),
            $"{things.Count} entries, e.g. '{(things.Count > 0 ? things[0].Label : "")}'");
        Check("thing picker values are the indices stored in the file",
            things.Select(i => i.Value).SequenceEqual(level.Things.Select(t => t.Num.ToString())));

        // With no archives the asset lists are empty rather than throwing.
        Check("asset picker with no catalog yields an empty list",
            PickerField.Assets(null, ".mat").Count == 0);
    }

    // ---- 6. Find dialog constructs, queries and reports results ----
    {
        var find = new FindWindow(level);
        find.Show();
        Dispatcher.UIThread.RunJobs();

        FindResult? chosen = null;
        find.ResultChosen = r => chosen = r;

        Check("find dialog renders", find.CaptureRenderedFrame() is not null);

        var hits = LevelQuery.Run(level, new FindQuery { Kind = FindKind.Thing, Text = "thing3" });
        Check("find locates a thing by name", hits.Count == 1 && hits[0].Thing?.Name == "thing3",
            $"{hits.Count} hit(s)");

        find.ResultChosen?.Invoke(hits[0]);
        Check("choosing a result raises ResultChosen", chosen is not null && chosen.Thing?.Name == "thing3");

        find.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ---- 6. Shift+drag pans instead of selecting ----
    selection.Clear();
    var panFrom = ScreenOf(new Vec3(-6, -6, 0));
    var panTo = new Point(panFrom.X + 60, panFrom.Y + 40);

    window.MouseDown(panFrom, MouseButton.Left, RawInputModifiers.Shift);
    window.MouseMove(panTo, RawInputModifiers.Shift);
    window.MouseUp(panTo, MouseButton.Left, RawInputModifiers.Shift);
    Dispatcher.UIThread.RunJobs();

    Check("shift+drag pans without selecting", selection.IsEmpty,
        $"{selection.Count} selected after a pan");

    LightInspectorProbe.Run(Check);
    SectorColormapProbe.Run(Check);
    ArchitectureInspectorProbe.Run(Check);
    EditingDiagnosticsProbe.Run(Check);
    UpdateRecoveryProbe.Run(Check);
    TextureWorkbenchProbe.Run(Check);
    ArrayPlacementProbe.Run(Check);

}, CancellationToken.None);

Console.WriteLine();
Console.WriteLine(failures.Count == 0
    ? "All UI checks passed."
    : $"{failures.Count} check(s) FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 2;

internal sealed class AppEntry
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

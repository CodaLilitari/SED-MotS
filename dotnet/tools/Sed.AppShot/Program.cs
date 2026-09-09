using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sed.App;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

// Real MainWindow/menu/viewport integration on Avalonia's offscreen platform.
// Settings and recovery outputs are isolated from the user's configuration.
string outPath = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "sed_app.png"));
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
string isolatedProject = Path.Combine(Path.GetDirectoryName(outPath)!, "appshot-isolated-session");
var session = HeadlessUnitTestSession.StartNew(typeof(AppEntry));
await session.Dispatch(() =>
{
    var settings = new AppSettings { AutoSave = false, ProjectDir = isolatedProject };
    var window = new MainWindow(settings) { Width = 1100, Height = 720 };
    try
    {
        window.Show(); // Avalonia.Headless creates no native desktop window.
        Dispatcher.UIThread.RunJobs();
        var menu = window.GetVisualDescendants().OfType<Menu>().Single();
        var file = menu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "_File");
        var newMots = file.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "New _MotS Level");
        newMots.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var document = window.CurrentDocument ?? throw new InvalidOperationException("New MotS did not create a document.");
        if (document.Level.Kind != ProjectType.MysteriesOfTheSith || document.Level.Sectors.Count != 1)
            throw new InvalidOperationException("New MotS did not create the expected editable MotS room.");
        using (var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame captured."))
            frame.Save(outPath);
        Console.WriteLine($"New MotS menu: editable document, {document.Level.Sectors.Count} sector, {document.Level.Sectors[0].Surfaces.Count} surfaces");
        Console.WriteLine($"Wrote {outPath}");
        var view = window.GetVisualDescendants().OfType<VulkanView>().Single();
        var vertex = document.Level.Sectors[0].Vertices[0];
        Vec3 before = vertex.Position;
        view.History.Do(new MoveVertexCommand(vertex, new Vec3(-1, 0, 0)));
        Dispatcher.UIThread.RunJobs();
        if (vertex.Position == before) throw new InvalidOperationException("The editable room did not respond to a vertex command.");
        string editedPath = Path.Combine(Path.GetDirectoryName(outPath)!, Path.GetFileNameWithoutExtension(outPath) + "-edited.png");
        using (var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No edited frame captured."))
            frame.Save(editedPath);
        Console.WriteLine($"Vertex edit: applied; wrote {editedPath}");
        view.History.Undo();
        if (vertex.Position != before) throw new InvalidOperationException("Undo did not restore the room vertex.");
        // Serialization checks that the UI room is a real document, not only a sample mesh.
        string roundtrip = JklWriter.Build(document);
        if (JklParser.ParseDocument(roundtrip).Level.Sectors.Count != 1)
            throw new InvalidOperationException("The UI's new room did not round-trip through JKL.");
        Console.WriteLine("Undo and JKL round-trip: OK");

        var originalThing = document.Level.NewThing();
        originalThing.Sector = document.Level.Sectors[0];
        originalThing.Layer = 3;
        originalThing.Flags = ThingFlags.NoEasy;
        originalThing.ArchLightingIndex = 7;
        originalThing.ArchLightingSourceModel = "synthetic.3do";
        originalThing.ArchLightingSourceModelHash = "synthetic-hash";
        originalThing.SourceParameters.Add(new("frame", "(0/0/0:0/0/0)"));
        originalThing.SourceParameters.Add(new("frame", "(1/0/0:0/0/0)"));
        view.Mode = EditMode.Thing;
        view.Selection.SelectOnly(originalThing);
        view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Insert });
        var clone = document.Level.Things.Last();
        if (ReferenceEquals(clone, originalThing) || clone.ArchLightingIndex != 7 || clone.Layer != 3
            || clone.Flags != originalThing.Flags || clone.ArchLightingSourceModelHash != "synthetic-hash"
            || clone.SourceParameters.Count != 2 || clone.Level != document.Level)
            throw new InvalidOperationException("Insert-key thing cloning lost metadata.");
        view.History.Undo();
        document.Level.Things.Remove(originalThing);
        Console.WriteLine("Insert-key thing clone preserves metadata; undo: OK");
    }
    finally { Console.WriteLine("Closing offscreen window…"); window.Close(); Console.WriteLine("Offscreen window closed."); }
}, CancellationToken.None);
Console.WriteLine("Offscreen dispatch completed.");
// Dispatch can resume the caller inline on its UI thread. Dispose on a worker
// so the session is never asked to join its own dispatcher thread.
await Task.Run(session.Dispose).ConfigureAwait(false);
Console.WriteLine("Offscreen session disposed.");
return 0;

internal sealed class AppEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

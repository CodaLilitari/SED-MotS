using System.Text;
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
using Sed.Formats.Cogs;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;

internal static class EditingDiagnosticsProbe
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool value) => report("editing diagnostics: " + name, value, "");
        var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
        var thing = level.NewThing(); thing.Sector = level.Sectors[0]; thing.Position = new Vec3(100, 100, 100);
        var checkWindow = new ConsistencyWindow(level);
        try
        {
            checkWindow.Show(); Dispatcher.UIThread.RunJobs();
            var check = checkWindow.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "CheckThingContainment");
            Check("expensive containment defaults off", check.IsChecked == false);
            check.Focus(); checkWindow.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            checkWindow.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            checkWindow.Run(); Dispatcher.UIThread.RunJobs();
            Check("keyboard enables explicit containment", check.IsChecked == true);
            Check("outside assignment warning rendered", checkWindow.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text?.Contains("not contained by its assigned", StringComparison.OrdinalIgnoreCase) == true));
            Check("diagnostic never moves or reassigns", thing.Position == new Vec3(100, 100, 100) && thing.Sector == level.Sectors[0]);
            checkWindow.SetLevel(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith)); Dispatcher.UIThread.RunJobs();
            Check("switching document refreshes consistency source", !checkWindow.GetVisualDescendants().OfType<TextBlock>()
                .Any(t => t.Text?.Contains("not contained by its assigned", StringComparison.OrdinalIgnoreCase) == true));
        }
        catch (Exception error) { report("editing diagnostics: containment UI flow", false, error.ToString()); }
        finally { checkWindow.Close(); }

        JklDocument? colorsDocument = new(LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith));
        var colorsWindow = new DynamicVertexColorsWindow(() => colorsDocument);
        try
        {
            string before = JklWriter.Build(colorsDocument);
            colorsWindow.Show(); Dispatcher.UIThread.RunJobs();
            var rows = colorsWindow.GetVisualDescendants().OfType<ListBox>().Single(c => c.Name == "VertexColorRows");
            Check("dynamic colors reports per-surface loader divergence", rows.Items.Count == 6 &&
                rows.Items.OfType<string>().All(s => s.Contains("Retail: Compacted; OpenMotS: Retained")));
            Check("dynamic colors does not edit the candidate", before == JklWriter.Build(colorsDocument));
            colorsDocument.Level.Sectors[0].Surfaces[0].Corners[0].MonoIntensity = .5f;
            var recheck = colorsWindow.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "RecheckVertexColors");
            recheck.Focus(); colorsWindow.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            colorsWindow.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
            Check("dynamic colors keyboard recheck observes edits", rows.Items.OfType<string>().First().Contains("Retail: Retained; OpenMotS: Retained"));
            colorsDocument = null; colorsWindow.Run(); Dispatcher.UIThread.RunJobs();
            Check("dynamic colors drops old results after document closes", rows.Items.Count == 0 &&
                colorsWindow.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("No active level document") == true));
        }
        catch (Exception error) { report("editing diagnostics: dynamic colors UI flow", false, error.ToString()); }
        finally { colorsWindow.Close(); }

        string directory = Path.Combine(Path.GetTempPath(), "sed-cog-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string archivePath = Path.Combine(directory, "test.goo");
            GobWriter.Build(archivePath, [("cog/test.cog", Encoding.ASCII.GetBytes("symbols\nsector trigger=0 linkid=3\nend\ncode\nend\n"))]);
            using var stream = File.OpenRead(archivePath);
            using var archive = new GobArchive(stream);
            var scripts = new CogScriptLibrary(new[] { archive });
            var cog = new Cog { Name = "test.cog" }; cog.Values.Add("0"); level.Cogs.Add(cog);
            var history = new EditHistory();
            var document = new JklDocument(level);
            var window = new CogEditorWindow(level, history, scripts, document: () => document);
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                var list = window.GetVisualDescendants().OfType<ListBox>().Single();
                list.SelectedIndex = 0; Dispatcher.UIThread.RunJobs();
                TextBox Field() => window.GetVisualDescendants().OfType<StackPanel>()
                    .Where(p => p.Children.FirstOrDefault() is TextBlock label && label.Text == "trigger  (sector)")
                    .SelectMany(p => p.GetVisualDescendants().OfType<TextBox>()).Single();
                var field = Field(); field.Focus(); field.Text = "-1";
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
                Check("typed COG UI rebinds through command", cog.Values[0] == "-1" && history.CanUndo);
                history.Undo(); window.Refresh(); Dispatcher.UIThread.RunJobs();
                Check("typed COG UI undo restores reference", cog.Values[0] == "0");
                field = Field(); field.Focus(); field.Text = "999";
                window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
                Check("typed COG UI refuses out-of-range target", cog.Values[0] == "0");
                Check("typed COG UI displays refusal", window.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.Name == "CogBindingError" && !string.IsNullOrEmpty(t.Text)));
                field.Text = "-1";
                window.CloseForResourceChange(); Dispatcher.UIThread.RunJobs();
                Check("resource reset discards stale uncommitted control text", cog.Values[0] == "0" && !history.CanUndo);
            }
            finally { window.Close(); }
        }
        catch (Exception error) { report("editing diagnostics: COG UI flow", false, error.ToString()); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

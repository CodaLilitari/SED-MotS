using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sed.App;
using Sed.Core.Model;
using Sed.Formats.Jkl;
using Sed.Formats.Launching;

/// <summary>Original Linux desktop startup and Save and Test fixtures, with no game assets or real engine.</summary>
internal static class LinuxDesktopProbe
{
    public static void Run(Action<string, bool, string> report)
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "sed-linux-ui-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "game with spaces"), project = Path.Combine(game, "project");
        string? previousConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(root, "config"));
        MainWindow? window = null;
        try
        {
            string configPath = (string)typeof(AppSettings).GetProperty("ConfigPath", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            if (configPath != Path.Combine(root, "config", "SED", "settings.json"))
                throw new InvalidOperationException("The test configuration directory was not isolated.");
            Directory.CreateDirectory(Path.Combine(game, "resource"));
            Directory.CreateDirectory(Path.Combine(project, "autosave"));
            File.WriteAllText(Path.Combine(game, "resource", "jk_.cd"), "original synthetic marker");
            string native = Path.Combine(game, "renamed native engine");
            File.WriteAllText(native, "#!/bin/sh\nprintf '%s\\n' \"$PWD\" \"$@\" > \"$0.args.tmp\"\nmv \"$0.args.tmp\" \"$0.args\"\n");
            File.SetUnixFileMode(native, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var level = LevelFactory.CreateUntexturedRoom(ProjectType.MysteriesOfTheSith);
            level.Header.Gravity = 9;
            string callerFile = Path.Combine(root, "caller.jkl");
            File.WriteAllText(callerFile, JklWriter.Build(new JklDocument(level)));
            byte[] original = File.ReadAllBytes(callerFile);
            level.Header.Gravity = 3;
            File.WriteAllText(Path.Combine(project, "autosave", "older.jkl"), JklWriter.Build(new JklDocument(level)));
            var settings = new AppSettings { ProjectDir = project, AutoSave = false, CheckForUpdates = false,
                MysteriesDir = game, MysteriesTestExecutable = native, MysteriesTestEngine = GameEngineProfile.OpenJKDF2 };
            string relativeCaller = Path.GetRelativePath(Environment.CurrentDirectory, callerFile);
            window = new MainWindow(settings, relativeCaller);
            Check("opens the relative caller file", window.CurrentDocument?.Level.Header.Gravity == 9
                && settings.RecentFiles.FirstOrDefault() == callerFile);
            window.Show(); Dispatcher.UIThread.RunJobs();
            Check("explicit file startup does not prompt to replace it with autosave", !window.OwnedWindows.Any(w => w.IsVisible));
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();

            SaveAndTest();
            string[]? nativeArguments = Receipt();
            Check("Save and Test starts the native profile with literal arguments", nativeArguments is not null
                && nativeArguments.SequenceEqual(new[] { game, "-devmode", "-debug", "log", "-motsCompat", "-path", "project" }));
            Check("native testing saves loose files and the matching archive",
                File.Exists(Path.Combine(project, "episode.jk")) && File.Exists(Path.Combine(project, "jkl", "caller.jkl"))
                && File.Exists(Path.Combine(project, "Test_caller.goo")));

            File.Delete(native + ".args");
            settings.MysteriesTestExecutable = "intentionally-missing-native";
            settings.TestCommand = "'" + native.Replace("'", "'\"'\"'") + "' explicit-template";
            SaveAndTest();
            string[]? templateArguments = Receipt();
            Check("an explicit legacy shell template still takes precedence", templateArguments is not null
                && templateArguments.SequenceEqual(new[] { game, "explicit-template" }));
            Check("startup and testing preserve the caller's source file", original.SequenceEqual(File.ReadAllBytes(callerFile)));

            void SaveAndTest() => typeof(MainWindow).GetMethod("SaveAndTest", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, null);
            string[]? Receipt()
            {
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!File.Exists(native + ".args") && DateTime.UtcNow < deadline) Thread.Sleep(10);
                return File.Exists(native + ".args") ? File.ReadAllLines(native + ".args") : null;
            }
            void Check(string label, bool ok) => report("Linux desktop: " + label, ok,
                ok ? "" : window?.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)
                    .FirstOrDefault(t => t?.StartsWith("Save-and-test failed:", StringComparison.Ordinal) == true) ?? "");
        }
        catch (Exception error) { report("Linux desktop: startup/test flow", false, error.ToString()); }
        finally
        {
            if (window is not null) { window.Close(); Dispatcher.UIThread.RunJobs(); }
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousConfig);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

using System.Reflection;
using Sed.App;
using Sed.Core.Model;
using Sed.Formats.Launching;

namespace Sed.Core.Tests;

[CollectionDefinition("Desktop settings environment", DisableParallelization = true)]
public sealed class DesktopSettingsEnvironmentCollection { }

/// <summary>Original native launch fixtures; no game installation or display is used.</summary>
[Collection("Desktop settings environment")]
public sealed class LinuxDesktopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sed-linux-desktop-" + Guid.NewGuid().ToString("N"));
    private string Game => Path.Combine(_root, "game with spaces");
    private string Project => Path.Combine(Game, "project");

    public LinuxDesktopTests()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Path.Combine(Game, "resource"));
        File.WriteAllText(Path.Combine(Game, "resource", "jk_.cd"), "original synthetic marker");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Executable(string name, bool executable = true)
    {
        string path = Path.Combine(Game, name);
        File.WriteAllText(path, "#!/bin/sh\nprintf '%s\\n' \"$PWD\" \"$@\" > \"$0.args.tmp\"\nmv \"$0.args.tmp\" \"$0.args\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | (executable ? UnixFileMode.UserExecute : UnixFileMode.None));
        return path;
    }

    private LaunchRequest Request(string? executable = null, GameEngineProfile engine = GameEngineProfile.Auto) =>
        new(ProjectType.MysteriesOfTheSith, Game, Project, "test.jkl", executable, engine);

    [Fact]
    public void NativeAutoPrefersTheNativeExecutableOverWindowsFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        string native = Executable("openjkdf2");
        Executable("openjkdf2-64.exe");
        Executable("jkm.exe");

        var plan = GameLauncher.PrepareLaunch(Request());

        Assert.Equal(native, plan.Executable);
        Assert.Equal(GameEngineProfile.OpenJKDF2, plan.Engine);
        Assert.Equal(new[] { "-devmode", "-debug", "log", "-motsCompat", "-path", "project" }, plan.Arguments);
        Assert.Equal(Game, plan.WorkingDirectory);
        Assert.Equal(new[] { "episode.jk", "jkl\\test.jkl" }, plan.RequiredLooseFiles);
        Assert.False(File.Exists(native + ".args")); // Preparing a plan never starts its executable.
    }

    [Fact]
    public void NativeAutoRecognizesThe64BitExecutableName()
    {
        if (!OperatingSystem.IsLinux()) return;
        string native = Executable("openjkdf2-64");
        Assert.Equal(native, GameLauncher.PrepareLaunch(Request()).Executable);
    }

    [Fact]
    public void RenamedNativeExecutableNeedsAnExplicitEngineProfile()
    {
        if (!OperatingSystem.IsLinux()) return;
        string native = Executable("renamed engine");
        Assert.Throws<ArgumentException>(() => GameLauncher.PrepareLaunch(Request(native)));
        Assert.Equal(native, GameLauncher.PrepareLaunch(Request(native, GameEngineProfile.OpenJKDF2)).Executable);
    }

    [Theory]
    [InlineData("openjkdf2-64.exe", GameEngineProfile.OpenJKDF2)]
    [InlineData("jkm.exe", GameEngineProfile.Retail)]
    public void NativePlanningRefusesWindowsExecutablesWithWineGuidance(string name, GameEngineProfile engine)
    {
        if (!OperatingSystem.IsLinux()) return;
        string executable = Executable(name);
        var error = Assert.Throws<ArgumentException>(() => GameLauncher.PrepareLaunch(Request(executable, engine)));
        Assert.Contains("Wine", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeExecutableWithoutExecutePermissionIsRejectedBeforeSaving()
    {
        if (!OperatingSystem.IsLinux()) return;
        string native = Executable("openjkdf2", executable: false);
        var error = Assert.Throws<ArgumentException>(() => GameLauncher.PrepareLaunch(Request(native)));
        Assert.Contains("executable permission", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeExecutablePathsDoNotAliasDifferentCase()
    {
        if (!OperatingSystem.IsLinux()) return;
        Executable("openjkdf2");
        Assert.Throws<ArgumentException>(() => GameLauncher.PrepareLaunch(
            Request(Path.Combine(Game, "OPENJKDF2"), GameEngineProfile.OpenJKDF2)));
    }

    [Fact]
    public async Task DirectNativeLaunchPreservesExecutableAndArgumentMetacharacters()
    {
        if (!OperatingSystem.IsLinux()) return;
        string native = Executable("engine's & literal");
        const string projectName = "project;&$(literal)";
        string project = Path.Combine(Game, projectName);
        Directory.CreateDirectory(project);
        var plan = GameLauncher.PrepareLaunch(Request(native, GameEngineProfile.OpenJKDF2) with { ProjectDirectory = project });
        var launch = typeof(MainWindow).GetMethod("LaunchTest", BindingFlags.NonPublic | BindingFlags.Static)!;

        launch.Invoke(null, [plan, null, Game]);
        string receipt = native + ".args";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(receipt) && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.True(File.Exists(receipt), "The original native fixture did not produce its argument receipt.");
        Assert.Equal(new[] { Game, "-devmode", "-debug", "log", "-motsCompat", "-path", projectName }, File.ReadAllLines(receipt));
    }

    [Fact]
    public void UnixRecentFilesKeepDistinctCaseSensitiveNames()
    {
        if (!OperatingSystem.IsLinux()) return;
        string config = Path.Combine(_root, "config");
        string? previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
        try
        {
            // Refuse to touch any real configuration if the runtime does not honor this isolated location.
            var path = (string)typeof(AppSettings).GetProperty("ConfigPath", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Assert.Equal(Path.Combine(config, "SED", "settings.json"), path);
            var settings = new AppSettings();
            string upper = Path.Combine(_root, "Level.jkl"), lower = Path.Combine(_root, "level.jkl");
            settings.AddRecent(upper);
            settings.AddRecent(lower);
            settings.AddRecent(upper);

            Assert.Equal(new[] { upper, lower }, settings.RecentFiles);
            Assert.Equal(new[] { upper, lower }, AppSettings.Load().RecentFiles);
        }
        finally { Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous); }
    }

    [Fact]
    public void DefaultProjectFolderUsesTheUserProfileWhenDocumentsIsUnavailable()
    {
        if (!OperatingSystem.IsLinux()) return;
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrEmpty(profile));
        string expected = Path.Combine(string.IsNullOrEmpty(documents) ? profile : documents, "SED");

        Assert.Equal(expected, new AppSettings().ResolvedProjectDir());
        Assert.NotEqual(Path.Combine(Path.GetTempPath(), "SED"), new AppSettings().ResolvedProjectDir());
    }

    [Fact]
    public void ExplicitWineTemplateStillExpandsTheExistingPlaceholders()
    {
        var settings = new AppSettings { MysteriesDir = Game,
            TestCommand = "wine \"{gameexe}\" -path \"{project}\" # {gob} {levelname} {game}" };
        string archive = Path.Combine(Project, "Test_test.goo");
        Assert.Equal($"wine \"{Path.Combine(Game, "jkm.exe")}\" -path \"{Project}\" # {archive} test {Game}",
            settings.BuildTestCommand(ProjectType.MysteriesOfTheSith, Project, archive, "test"));
    }
}

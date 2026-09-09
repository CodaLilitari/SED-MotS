using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Launching;

namespace Sed.App;

/// <summary>
/// Persisted editor settings — per-game installation directories, mirroring the
/// original editor's JK/MotS/IJIM path options. Stored as JSON under the user's
/// application-data directory.
/// </summary>
public sealed class AppSettings
{
    public string? JediKnightDir { get; set; }
    public string? MysteriesDir { get; set; }
    public string? InfernalMachineDir { get; set; }
    public string? JediKnightTestExecutable { get; set; }
    public string? MysteriesTestExecutable { get; set; }
    public GameEngineProfile JediKnightTestEngine { get; set; } = GameEngineProfile.Auto;
    public GameEngineProfile MysteriesTestEngine { get; set; } = GameEngineProfile.Auto;

    /// <summary>
    /// Where "Save and Test" writes loose episode/JKL files and a GOB/GOO. ProjectDir is
    /// the directory the game's <c>-path</c> points at so the saved level is found.
    /// </summary>
    public string? ProjectDir { get; set; }

    /// <summary>
    /// Command template used to launch the game for testing (macOS/Linux, where
    /// the original's .bat cannot run). Placeholders are expanded from the
    /// session: <c>{project}</c> <c>{gob}</c> <c>{game}</c> <c>{gameexe}</c>
    /// <c>{levelname}</c>. Run through the shell, so Wine/CrossOver invocations
    /// work as-is (their paths are <c>Z:\&lt;mac path&gt;</c>).
    /// </summary>
    public string? TestCommand { get; set; }

    /// <summary>Autosave on a timer (the original's SaveTimer + AutoSave option).</summary>
    public bool AutoSave { get; set; } = true;

    /// <summary>Autosave interval in minutes (the original's SaveInterval).</summary>
    public int AutoSaveIntervalMinutes { get; set; } = 5;

    /// <summary>Recently opened loose level files (most recent first).</summary>
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Command-key overrides: action name → semicolon-separated gesture strings.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = new();

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolderOption.Create),
        "SED", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var path = ConfigPath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch { /* corrupt/unreadable — start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }

    public string? DirFor(ProjectType game) => game switch
    {
        ProjectType.MysteriesOfTheSith => MysteriesDir,
        ProjectType.InfernalMachine => InfernalMachineDir,
        _ => JediKnightDir,
    };

    public string? TestExecutableFor(ProjectType game) => game == ProjectType.MysteriesOfTheSith
        ? MysteriesTestExecutable : JediKnightTestExecutable;
    public GameEngineProfile TestEngineFor(ProjectType game) => game == ProjectType.MysteriesOfTheSith
        ? MysteriesTestEngine : JediKnightTestEngine;

    public void SetDir(ProjectType game, string dir)
    {
        switch (game)
        {
            case ProjectType.MysteriesOfTheSith: MysteriesDir = dir; break;
            case ProjectType.InfernalMachine: InfernalMachineDir = dir; break;
            default: JediKnightDir = dir; break;
        }
    }

    /// <summary>
    /// The project directory for "Save and Test" — the configured one, else a
    /// default under the user's Documents so the feature works unconfigured.
    /// </summary>
    public string ResolvedProjectDir()
    {
        if (!string.IsNullOrWhiteSpace(ProjectDir)) return ProjectDir;
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(string.IsNullOrEmpty(docs) ? Path.GetTempPath() : docs, "SED");
    }

    /// <summary>The game executable, as the original's test batch used per game.</summary>
    public static string GameExeName(ProjectType game) => game switch
    {
        ProjectType.MysteriesOfTheSith => "jkm.exe",
        ProjectType.InfernalMachine => "ijm.exe",
        _ => "jk.exe",
    };

    /// <summary>
    /// Expands the configured test-command template against the current session.
    /// Returns null when no command is configured.
    /// </summary>
    public string? BuildTestCommand(ProjectType game, string projectDir, string gobPath, string levelName)
    {
        var template = TestCommand;
        if (string.IsNullOrWhiteSpace(template)) return null;

        var gameDir = DirFor(game) ?? string.Empty;
        return template
            .Replace("{project}", projectDir)
            .Replace("{gob}", gobPath)
            .Replace("{game}", gameDir)
            .Replace("{gameexe}", Path.Combine(gameDir, GameExeName(game)))
            .Replace("{levelname}", levelName);
    }

    /// <summary>Records a recently opened level file (most recent first, capped).</summary>
    public void AddRecent(string path)
    {
        RecentFiles.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > 12) RecentFiles.RemoveRange(12, RecentFiles.Count - 12);
        Save();
    }
}

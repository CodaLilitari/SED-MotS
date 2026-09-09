using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Sed.Core.Model;

namespace Sed.Formats.Launching;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameEngineProfile { Auto, OpenJKDF2, Retail }

public sealed record LaunchRequest(ProjectType Game, string GameDirectory, string ProjectDirectory,
    string LevelFileName, string? Executable = null, GameEngineProfile Engine = GameEngineProfile.Auto);

/// <summary>A reviewable, serializable plan. Preparing it never writes files or starts a process.</summary>
public sealed record GameLaunchPlan(string Executable, string WorkingDirectory, string ProjectDirectory,
    GameEngineProfile Engine, string ProjectPathToken, IReadOnlyList<string> Arguments,
    IReadOnlyList<string> RequiredLooseFiles);

/// <summary>Read-only host facts, injectable so planning can be tested without a game installation.</summary>
public interface ILaunchFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    string? ShortPath(string path);
}

public static class GameLauncher
{
    // OpenJKDF2 e9e293a: Main_path and jkRes path buffers contain 0x80 bytes,
    // and the engine tokenizes its command line by whitespace without honoring quotes.
    public const int MaximumResourcePathBytes = 127;
    private static readonly string[] OpenJkNames = ["openjkdf2-64.exe", "openjkdf2.exe", "openjkdf2-32.exe"];

    public static GameLaunchPlan PrepareLaunch(LaunchRequest request, ILaunchFileSystem? fileSystem = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        fileSystem ??= PhysicalLaunchFileSystem.Instance;
        if (request.Game is not (ProjectType.JediKnight or ProjectType.MysteriesOfTheSith))
            throw new NotSupportedException("This launch planner supports Jedi Knight and Mysteries of the Sith.");
        if (!Enum.IsDefined(request.Engine)) throw new ArgumentException("Unknown engine profile.");
        TestEpisode.ValidateLevelFileName(request.LevelFileName);
        if (string.IsNullOrWhiteSpace(request.GameDirectory) || !fileSystem.DirectoryExists(request.GameDirectory))
            throw new ArgumentException("The configured game folder does not exist. Set it in Game > Set Folder.");
        if (string.IsNullOrWhiteSpace(request.ProjectDirectory) || !fileSystem.DirectoryExists(request.ProjectDirectory))
            throw new ArgumentException("The test project folder does not exist.");

        string gameDir = Path.GetFullPath(request.GameDirectory);
        string projectDir = Path.GetFullPath(request.ProjectDirectory);
        var (executable, engine) = ResolveExecutable(request, gameDir, fileSystem);
        if (engine == GameEngineProfile.OpenJKDF2 && !fileSystem.FileExists(Path.Combine(gameDir, "resource", "jk_.cd")))
            throw new ArgumentException("OpenJKDF2 needs resource/jk_.cd in the game folder; without it the install helper may change the working directory.");

        string[] requiredFiles = ["episode.jk", "jkl\\" + request.LevelFileName];
        int reservedSuffix = requiredFiles.Max(p => p.Length + 1);
        var candidates = new[] { Path.GetRelativePath(gameDir, projectDir), projectDir, fileSystem.ShortPath(projectDir) };
        string? pathToken = candidates.Where(p => IsEnginePathToken(p) && p!.Length + reservedSuffix <= MaximumResourcePathBytes)
            .OrderBy(p => p!.Length).FirstOrDefault();
        if (pathToken is null)
            throw new ArgumentException($"The engine cannot read this test path. Choose a shorter project folder with ASCII characters and no spaces. The path plus its longest required resource name must fit {MaximumResourcePathBytes} bytes.");

        var arguments = new List<string> { "-devmode", "-debug", "log" };
        if (engine == GameEngineProfile.OpenJKDF2 && request.Game == ProjectType.MysteriesOfTheSith)
            arguments.Add("-motsCompat");
        arguments.Add("-path");
        arguments.Add(pathToken);
        return new GameLaunchPlan(executable, gameDir, projectDir, engine, pathToken, arguments, requiredFiles);
    }

    public static bool IsEnginePathToken(string? value) => !string.IsNullOrEmpty(value)
        && value.Length <= MaximumResourcePathBytes && value.All(c => c is >= '!' and <= '~' && c != '"');

    private static (string executable, GameEngineProfile engine) ResolveExecutable(LaunchRequest request,
        string gameDir, ILaunchFileSystem fileSystem)
    {
        string retailName = request.Game == ProjectType.MysteriesOfTheSith ? "jkm.exe" : "jk.exe";
        if (!string.IsNullOrWhiteSpace(request.Executable))
        {
            string configured = request.Executable.Trim();
            if (configured.Contains('"') || !Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Select an executable file without command-line arguments or surrounding quotes.");
            string fullPath = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(gameDir, configured));
            if (!fileSystem.FileExists(fullPath)) throw new ArgumentException("The selected game executable does not exist.");
            var engine = request.Engine;
            if (engine == GameEngineProfile.Auto)
            {
                string name = Path.GetFileName(fullPath);
                if (OpenJkNames.Contains(name, StringComparer.OrdinalIgnoreCase)) engine = GameEngineProfile.OpenJKDF2;
                else if (name.Equals(retailName, StringComparison.OrdinalIgnoreCase)) engine = GameEngineProfile.Retail;
                else throw new ArgumentException("For a renamed executable, explicitly choose OpenJKDF2 or Retail in Test Setup.");
            }
            return (fullPath, engine);
        }

        if (request.Engine is GameEngineProfile.Auto or GameEngineProfile.OpenJKDF2)
            foreach (string name in OpenJkNames)
            {
                string candidate = Path.Combine(gameDir, name);
                if (fileSystem.FileExists(candidate)) return (candidate, GameEngineProfile.OpenJKDF2);
            }
        if (request.Engine is GameEngineProfile.Auto or GameEngineProfile.Retail)
        {
            string candidate = Path.Combine(gameDir, retailName);
            if (fileSystem.FileExists(candidate)) return (candidate, GameEngineProfile.Retail);
        }
        throw new ArgumentException($"No executable matches the {request.Engine} profile in the game folder. Select the executable in Test Setup.");
    }

    private sealed class PhysicalLaunchFileSystem : ILaunchFileSystem
    {
        public static readonly PhysicalLaunchFileSystem Instance = new();
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public bool FileExists(string path) => File.Exists(path);
        public string? ShortPath(string path)
        {
            if (!OperatingSystem.IsWindows()) return null;
            var buffer = new StringBuilder(32768);
            uint count = GetShortPathName(path, buffer, (uint)buffer.Capacity);
            return count > 0 && count < buffer.Capacity ? buffer.ToString() : null;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetShortPathNameW", SetLastError = true)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferLength);
    }
}

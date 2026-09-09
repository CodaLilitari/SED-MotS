using Sed.Core.Model;
using Sed.Formats.Gob;

namespace Sed.Formats.Game;

/// <summary>
/// A located game installation: opens the level + resource archives under a base
/// install directory, mirroring the original editor's per-game path settings and
/// FindGobJK/FindGoo logic (it searched <c>&lt;base&gt;\Episode\</c> and
/// <c>&lt;base&gt;\Resource\</c>). Lookups are case-insensitive so the same paths
/// work on case-sensitive volumes.
/// </summary>
public sealed class GameInstall : IDisposable
{
    public ProjectType Game { get; }
    public string BaseDir { get; }
    public GobArchive? LevelArchive { get; }
    public IReadOnlyList<GobArchive> ResourceArchives { get; }

    private readonly List<GobArchive> _all = new();

    private GameInstall(ProjectType game, string baseDir, GobArchive? levels, List<GobArchive> resources)
    {
        Game = game;
        BaseDir = baseDir;
        LevelArchive = levels;
        ResourceArchives = resources;
        if (levels is not null) _all.Add(levels);
        _all.AddRange(resources);
    }

    /// <summary>The resource GOB file names searched under a base dir, per game.</summary>
    public static (string[] level, string[] resources) ExpectedFiles(ProjectType game) => game switch
    {
        ProjectType.MysteriesOfTheSith => (new[] { "Jkm.goo" }, new[] { "Jkmres.goo", "JKMsndLO.goo" }),
        ProjectType.InfernalMachine => (Array.Empty<string>(), new[] { "CD2.GOB", "CD1.GOB" }),
        _ => (new[] { "Jk1.gob" }, new[] { "Res2.gob", "Res1hi.gob", "Res1low.gob" }),
    };

    /// <summary>Opens the install at <paramref name="baseDir"/>, or null if no archives are found.</summary>
    public static GameInstall? TryOpen(ProjectType game, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return null;
        var (levelNames, resourceNames) = ExpectedFiles(game);

        GobArchive? levels = null;
        var resources = new List<GobArchive>();
        try
        {
            foreach (var name in levelNames)
            {
                var path = FindFile(baseDir, name);
                if (path is not null) { levels = GobArchive.Open(path); break; }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in resourceNames)
            {
                var path = FindFile(baseDir, name);
                if (path is not null && seen.Add(Path.GetFileName(path)))
                    resources.Add(GobArchive.Open(path));
            }

            if (levels is null && resources.Count == 0) return null;
            return new GameInstall(game, baseDir, levels, resources);
        }
        catch
        {
            levels?.Dispose();
            foreach (var archive in resources) archive.Dispose();
            throw;
        }
    }

    /// <summary>Whether a base dir looks like a valid install (has at least one expected archive).</summary>
    public static bool IsValid(ProjectType game, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(baseDir)) return false;
        var (level, resources) = ExpectedFiles(game);
        return level.Concat(resources).Any(n => FindFile(baseDir, n) is not null);
    }

    public IEnumerable<GobEntry> Levels =>
        LevelArchive?.FindByExtension(".jkl").OrderBy(e => e.NormalizedName)
        ?? Enumerable.Empty<GobEntry>();

    /// <summary>Reads a level's text by entry.</summary>
    public string ReadLevel(GobEntry entry) => LevelArchive!.ReadText(entry);

    /// <summary>Searches base dir, then <c>Episode\</c> and <c>Resource\</c>, case-insensitively.</summary>
    private static string? FindFile(string baseDir, string fileName)
    {
        foreach (var sub in new[] { "", "Episode", "Resource" })
        {
            var dir = sub.Length == 0 ? baseDir : FindDir(baseDir, sub);
            if (dir is null) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        return null;
    }

    private static string? FindDir(string baseDir, string name)
    {
        foreach (var d in Directory.EnumerateDirectories(baseDir))
            if (Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase))
                return d;
        return null;
    }

    public void Dispose()
    {
        foreach (var gob in _all) gob.Dispose();
        _all.Clear();
    }
}

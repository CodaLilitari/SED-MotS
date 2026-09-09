using Sed.Core.Model;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;

namespace Sed.Formats.Game;

/// <summary>
/// Owns a fully parsed candidate document and its resource handles. Construct this
/// before discarding a live editor session, and dispose it if preparation fails.
/// Archive order is explicit: selected addon, base episode, then base resources.
/// </summary>
public sealed class PreparedLevelOpen : IDisposable
{
    private GobArchive? _addon;
    public GameInstall? Install { get; private set; }
    public GobArchive? LevelArchive { get; private set; }
    public IReadOnlyList<GobArchive> Resources { get; private set; } = [];
    public IReadOnlyList<GobEntry> Levels { get; private set; } = [];
    public JklDocument Document { get; private set; } = null!;
    public string LevelName { get; private set; } = string.Empty;

    private PreparedLevelOpen() { }

    public static PreparedLevelOpen FromPath(string path, Func<ProjectType, string?> configuredDirectory)
    {
        var result = new PreparedLevelOpen();
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (Path.GetExtension(path).Equals(".gob", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".goo", StringComparison.OrdinalIgnoreCase))
            {
                result._addon = GobArchive.Open(fullPath);
                result.LevelArchive = result._addon;
                result.ParseFirstLevel();
            }
            else
            {
                result.Document = new[] { ".jed", ".sed", ".ned" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
                    ? Sed.Formats.Jed.JedImporter.ImportFile(fullPath).Document
                    : JklParser.ParseDocument(LegacyText.ReadFile(fullPath));
                result.LevelName = Path.GetFileName(fullPath);
            }
            RequireEditableLevel(result.Document);

            var game = result.Document.Level.Kind;
            string? directory = Path.GetDirectoryName(fullPath);
            string? parent = directory is null ? null : Directory.GetParent(directory)?.FullName;
            foreach (string? candidate in new[] { configuredDirectory(game), directory, parent,
                parent is null ? null : Directory.GetParent(parent)?.FullName }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                result.Install = GameInstall.TryOpen(game, candidate);
                if (result.Install is not null) break;
            }
            result.SetResources();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static PreparedLevelOpen FromGame(ProjectType game, string directory)
    {
        var result = new PreparedLevelOpen();
        try
        {
            result.Install = GameInstall.TryOpen(game, directory)
                ?? throw new FileNotFoundException("No game archives were found in the configured folder.");
            result.LevelArchive = result.Install.LevelArchive;
            result.ParseFirstLevel();
            result.SetResources();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static PreparedLevelOpen FromDocument(JklDocument document, string levelName, string? gameDirectory = null)
    {
        RequireEditableLevel(document);
        var result = new PreparedLevelOpen { Document = document, LevelName = levelName };
        try
        {
            if (!string.IsNullOrWhiteSpace(gameDirectory)) result.Install = GameInstall.TryOpen(document.Level.Kind, gameDirectory);
            result.SetResources();
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private void ParseFirstLevel()
    {
        Levels = LevelArchive?.FindByExtension(".jkl").OrderBy(e => e.NormalizedName).ToArray() ?? [];
        if (Levels.Count == 0) throw new InvalidDataException("This archive contains no editable JKL levels; the current document was preserved.");
        var entry = Levels[0];
        Document = JklParser.ParseDocument(LevelArchive!.ReadText(entry));
        RequireEditableLevel(Document);
        LevelName = Path.GetFileName(entry.NormalizedName);
    }

    private static void RequireEditableLevel(JklDocument document)
    {
        if (document.Level.Sectors.Count == 0)
            throw new InvalidDataException("The input contains no editable sectors; the current document was preserved.");
    }

    private void SetResources()
    {
        var resources = new List<GobArchive>();
        if (_addon is not null) resources.Add(_addon);
        if (Install?.LevelArchive is { } episode) resources.Add(episode);
        if (Install is not null) resources.AddRange(Install.ResourceArchives);
        Resources = resources.Distinct().ToArray();
    }

    public void Dispose()
    {
        _addon?.Dispose();
        _addon = null;
        Install?.Dispose();
        Install = null;
    }
}

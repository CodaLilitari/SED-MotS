using System.Text;
using Sed.Core.Model;
using Sed.Formats.Game;

namespace Sed.Formats.Launching;

public sealed record TestEpisodePackage(string LevelFileName, string ArchiveExtension, string EpisodeText,
    IReadOnlyList<TestEpisodeEntry> Entries);
public sealed record TestEpisodeEntry(string Name, byte[] Data);

/// <summary>Builds the loose override and archive entries used by Save and Test; no filesystem changes.</summary>
public static class TestEpisode
{
    // jkEpisode's sequence filename is a 32-byte C string, including its terminator.
    public static string SafeLevelName(string name)
    {
        string safe = new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').Take(27).ToArray());
        return string.IsNullOrEmpty(safe) ? "level" : safe;
    }

    public static void ValidateLevelFileName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length > 31 || !fileName.EndsWith(".jkl", StringComparison.OrdinalIgnoreCase)
            || fileName.Length <= 4 || fileName[..^4].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
            throw new ArgumentException("Use a level filename with up to 27 ASCII letters, digits, underscores or hyphens, followed by .jkl.");
    }

    public static TestEpisodePackage Build(ProjectType game, string levelFileName, string jklText, string? existingEpisode = null)
    {
        if (game is not (ProjectType.JediKnight or ProjectType.MysteriesOfTheSith))
            throw new NotSupportedException("Test episode packaging supports Jedi Knight and Mysteries of the Sith.");
        ValidateLevelFileName(levelFileName);
        string episodeText;
        if (existingEpisode is not null)
        {
            var episode = EpisodeFile.Parse(existingEpisode);
            if (!episode.Sequences.Any(s => s.Type.Equals("LEVEL", StringComparison.OrdinalIgnoreCase)
                    && s.File.Equals(levelFileName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"The project already has an episode.jk that does not reference {levelFileName}. Add the level to that episode, or choose another test project folder. The existing episode was preserved.");
            episodeText = existingEpisode;
        }
        else
        {
            var episode = new EpisodeFile { Name = "SED test: " + levelFileName[..^4], GameType = 1 };
            episode.Sequences.Add(new EpisodeSequence { Line = 0, Cd = 1, LevelNum = 1, File = levelFileName });
            episodeText = episode.Build();
        }

        // Lossy ASCII replacement would silently corrupt resource names and source text.
        var encoding = Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return new TestEpisodePackage(levelFileName, game == ProjectType.MysteriesOfTheSith ? ".goo" : ".gob", episodeText,
            [new("episode.jk", encoding.GetBytes(episodeText)), new("jkl\\" + levelFileName, encoding.GetBytes(jklText))]);
    }
}

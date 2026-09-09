using System.Security.Cryptography;
using System.Text;
using Sed.Formats.Jkl;
using Sed.Formats.Launching;

namespace Sed.Formats.Game;

/// <summary>Non-destructive snapshots used when switching away from an edited document.</summary>
public static class DocumentRecovery
{
    public static string Fingerprint(JklDocument document) => FingerprintText(JklWriter.Build(document));
    public static string FingerprintText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Save(string recoveryDirectory, string levelName, string text)
    {
        Directory.CreateDirectory(recoveryDirectory);
        string path = Path.Combine(recoveryDirectory,
            $"{TestEpisode.SafeLevelName(levelName)}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.jkl");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
        writer.Flush();
        stream.Flush(flushToDisk: true);
        return path;
    }
}

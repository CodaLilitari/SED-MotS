using System.Security.Cryptography;
using System.Text;

namespace Sed.Formats.ThreeDo;

/// <summary>The existing Windows Delphi fork's normalized-text model baseline, distinct from a raw archive SHA256.</summary>
public static class ArchitectureModelFingerprint
{
    /// <summary>
    /// Matches ASCII TTextFile/RemoveComment/Trim/TStringList.Text/THashSHA2 semantics.
    /// Non-ASCII depends on the original Windows ANSI code page; do not invent equivalence.
    /// </summary>
    public static string? Compute(string source)
    {
        var normalized = new StringBuilder();
        foreach (string line in source.Split('\n'))
        {
            if (line.Length > 4095 || line.Any(c => c == '\0' || c > 127)) return null;
            int comment = line.IndexOf('#');
            string content = comment >= 0 ? line[..comment] : line;
            // Delphi SysUtils.Trim removes every boundary character <= U+0020,
            // including ASCII controls that .NET's Unicode Trim retains.
            int start = 0, end = content.Length;
            while (start < end && content[start] <= ' ') start++;
            while (end > start && content[end - 1] <= ' ') end--;
            content = content[start..end];
            if (content.Length > 0) normalized.Append(content).Append("\r\n");
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToString()))).ToLowerInvariant();
    }
}

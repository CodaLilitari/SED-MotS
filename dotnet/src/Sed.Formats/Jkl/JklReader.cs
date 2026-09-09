using System.Globalization;

namespace Sed.Formats.Jkl;

/// <summary>
/// Low-level line reader for the JKL text format, mirroring GetNextLine in the
/// original SED loader (LEVEL_IO.INC): strips '#' comments, trims, optionally
/// upper-cases, and tracks SECTION:/END boundaries.
/// </summary>
public sealed class JklReader
{
    private readonly TextReader _reader;

    public string Current { get; private set; } = string.Empty;
    public string Section { get; private set; } = string.Empty;
    public bool EndOfSection { get; private set; }
    public bool Eof { get; private set; }
    public int LineNumber { get; private set; }

    public JklReader(TextReader reader) => _reader = reader;

    /// <summary>Reads the next non-empty line. Returns false at end of file.</summary>
    public bool Next(bool upper = true, bool preserveComment = false)
    {
        Current = string.Empty;
        while (true)
        {
            var line = _reader.ReadLine();
            if (line is null)
            {
                Eof = true;
                EndOfSection = true;
                return false;
            }
            LineNumber++;

            int hash = line.IndexOf('#');
            if (hash >= 0 && !preserveComment) line = line[..hash];
            line = line.Trim();
            if (upper) line = line.ToUpperInvariant();
            if (line.Length == 0) continue;

            Current = line;
            break;
        }

        if (string.Equals(Current, "END", StringComparison.OrdinalIgnoreCase))
        {
            Section = string.Empty;
            EndOfSection = true;
        }
        var first = FirstWord(Current);
        if (string.Equals(first, "SECTION:", StringComparison.OrdinalIgnoreCase))
        {
            Section = WordAt(Current, 1).ToUpperInvariant();
            EndOfSection = true;
        }
        return true;
    }

    /// <summary>Advances until a SECTION: header is found or EOF; used by the dispatch loop.</summary>
    public void SeekSection()
    {
        while (Section.Length == 0 && !Eof)
            Next();
    }

    /// <summary>Marks the current section as being consumed: clears the name and the end flag.</summary>
    public void EnterSection()
    {
        Section = string.Empty;
        EndOfSection = false;
    }

    public void SkipToNextSection()
    {
        while (!EndOfSection) Next();
    }

    // ---- token helpers ----

    public static string[] Tokens(string line) =>
        line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whitespace-separated tokens, retaining spaces inside frame/vector parentheses.</summary>
    public static string[] ParameterTokens(string line)
    {
        var tokens = new List<string>();
        int start = -1, depth = 0;
        for (int i = 0; i <= line.Length; i++)
        {
            char c = i == line.Length ? ' ' : line[i];
            if (depth == 1 && start >= 0 && char.IsWhiteSpace(c) &&
                IsLegacyUnterminatedEyeOffsetToken(line[start..i]) &&
                IsParameterBoundary(line.AsSpan(i)))
            {
                tokens.Add(line[start..i]);
                start = -1;
                depth = 0;
                continue;
            }
            if (depth == 0 && char.IsWhiteSpace(c))
            {
                if (start >= 0) { tokens.Add(line[start..i]); start = -1; }
                continue;
            }
            if (start < 0) start = i;
            if (c == '(' && ++depth > 32) throw new FormatException("JKL parameter nesting exceeds the parser limit.");
            if (c == ')' && --depth < 0) throw new FormatException("Unmatched JKL parameter parenthesis.");
        }
        if (depth != 0)
        {
            string finalToken = start >= 0 ? line[start..].TrimEnd() : string.Empty;
            if (depth != 1 || !(IsLegacyUnterminatedFrameToken(finalToken) || IsLegacyUnterminatedEyeOffsetToken(finalToken)))
                throw new FormatException("Unclosed JKL parameter parenthesis.");
            tokens.Add(finalToken);
        }
        return tokens.ToArray();
    }

    // sithTrackThing accepts six successful sscanf conversions even when the final
    // literal ')' is absent. A released community level uses precisely this form.
    // Keep its spelling; do not extend this exception to other malformed vectors.
    internal static bool IsLegacyUnterminatedFrameToken(string token)
    {
        if (!token.StartsWith("frame=(", StringComparison.OrdinalIgnoreCase) || token.EndsWith(')')) return false;
        var halves = token[7..].Split(':');
        if (halves.Length != 2) return false;
        foreach (string half in halves)
        {
            var values = half.Split('/');
            if (values.Length != 3 || values.Any(value =>
                !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number)))
                return false;
        }
        return true;
    }

    // stdConffile splits parameters before sithActor scans eyeoffset. Three
    // successful float conversions survive a missing final literal ')'. TODOA
    // uses this spelling immediately before cog=; preserve both parameters.
    internal static bool IsLegacyUnterminatedEyeOffsetToken(string token)
    {
        if (!token.StartsWith("eyeoffset=(", StringComparison.OrdinalIgnoreCase)) return false;
        var values = token[11..].Split('/');
        return values.Length == 3 && values.All(value => value.Length > 0 &&
            !value.Any(char.IsWhiteSpace) &&
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number) && float.IsFinite(number));
    }

    private static bool IsParameterBoundary(ReadOnlySpan<char> remaining)
    {
        remaining = remaining.TrimStart();
        if (remaining.IsEmpty) return true;
        int i = 0;
        while (i < remaining.Length && (char.IsAsciiLetterOrDigit(remaining[i]) || remaining[i] == '_')) i++;
        return i > 0 && i < remaining.Length && remaining[i] == '=';
    }

    public static string FirstWord(string line) => WordAt(line, 0);

    public static string WordAt(string line, int index)
    {
        var t = Tokens(line);
        return index < t.Length ? t[index] : string.Empty;
    }

    /// <summary>Drops a leading "N:" index token (e.g. "12:") if present.</summary>
    public static string StripIndex(string line)
    {
        int colon = line.IndexOf(':');
        return colon >= 0 ? line[(colon + 1)..].Trim() : line;
    }

    public static double ParseFloat(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v : throw new FormatException($"Invalid finite JKL number: '{s}'.");

    public static int ParseInt(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : throw new FormatException($"Invalid JKL integer: '{s}'.");

    public static long ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0X", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? v : throw new FormatException($"Invalid JKL flags: '{s}'.");
    }
}

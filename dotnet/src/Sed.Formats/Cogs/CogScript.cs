using System.Globalization;
using System.Text.RegularExpressions;
using Sed.Core.Validation;

namespace Sed.Formats.Cogs;

/// <summary>
/// COG symbol types (`CogTypes` in `VALUES.PAS`). The type decides what a value
/// refers to — a thing index, a sector index, a sound filename, and so on.
/// </summary>
public enum CogSymbolType
{
    Unknown,
    Message,
    Int,
    Flex,
    Float,
    Vector,
    Thing,
    Sector,
    Surface,
    Sound,
    Material,
    Model,
    Template,
    Keyframe,
    Ai,
    Cog,
}

/// <summary>One entry of a script's <c>symbols</c> block.</summary>
public sealed record CogSymbol(
    CogSymbolType Type,
    string Name,
    string? Default,
    bool Local,
    string? Description)
{
    /// <summary>Exact source line, including its spelling, spacing and comment.</summary>
    public string RawDeclaration { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    /// <summary>Modifier text in source order; unknown modifiers are retained.</summary>
    public IReadOnlyList<string> RawModifiers { get; init; } = Array.Empty<string>();
    /// <summary>Whether a nolink modifier occurred. A later linkid can override it.</summary>
    public bool NoLink { get; init; }
    public int? ExplicitLinkId { get; init; }
    public uint? ExplicitMask { get; init; }
    /// <summary>OpenJKDF2 reference-symbol interpretation; not proof of a runtime subscription.</summary>
    public int EffectiveLinkId { get; init; }
    public uint EffectiveMask { get; init; } = 0x401;

    /// <summary>
    /// True when the level supplies this symbol's value. Locals are script-private
    /// and messages are entry points, so neither appears in the JKL's COGS line.
    /// </summary>
    public bool TakesLevelValue => !Local && Type != CogSymbolType.Message;
}

/// <summary>A source-layout problem that prevents reliable positional typed edits.</summary>
public sealed record CogSchemaDiagnostic(string Code, int? LineNumber, string Message, string? RawDeclaration = null);

/// <summary>
/// A parsed <c>.cog</c> script — specifically its <c>symbols</c> block, which is
/// what turns a placed COG's bare positional values in the JKL into named,
/// typed parameters.
///
/// The correspondence was verified against retail data: a placed COG's values map
/// in order onto the script's symbols that are neither <c>local</c> nor
/// <c>message</c>. Across `01narshadda`, `03katarn`, `07yun` and `09fuelstation`
/// that matched for every one of the 96 placed COGs whose script was resolvable,
/// with no mismatches.
/// </summary>
public sealed class CogScript
{
    public string Name { get; }
    public IReadOnlyList<CogSymbol> Symbols { get; }
    public string SourceText { get; }
    public bool HasSymbolsBlock { get; }
    public bool HasSymbolsEnd { get; }
    public IReadOnlyList<CogSchemaDiagnostic> Diagnostics { get; }
    public IReadOnlyList<string> RawSymbolDeclarations { get; }
    /// <summary>Only a completely understood symbol layout is eligible for typed positional edits.</summary>
    public bool IsComplete => HasSymbolsBlock && HasSymbolsEnd && Diagnostics.Count == 0;

    /// <summary>Symbols the level supplies values for, in the order they appear.</summary>
    public IReadOnlyList<CogSymbol> LevelValues { get; }

    private CogScript(string name, string sourceText, List<CogSymbol> symbols,
        bool hasSymbolsBlock, bool hasSymbolsEnd, List<CogSchemaDiagnostic> diagnostics,
        List<string> rawDeclarations)
    {
        Name = name;
        SourceText = sourceText;
        Symbols = symbols.AsReadOnly();
        LevelValues = symbols.Where(s => s.TakesLevelValue).ToList().AsReadOnly();
        HasSymbolsBlock = hasSymbolsBlock;
        HasSymbolsEnd = hasSymbolsEnd;
        Diagnostics = diagnostics.AsReadOnly();
        RawSymbolDeclarations = rawDeclarations.AsReadOnly();
    }

    public static CogScript Parse(string name, string text)
    {
        var symbols = new List<CogSymbol>();
        var diagnostics = new List<CogSchemaDiagnostic>();
        var declarations = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool inSymbols = false;
        bool found = false, ended = false;
        int lineNumber = 0;

        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = StripComment(raw, out var comment).Trim().TrimStart('\uFEFF');

            if (line.Equals("symbols", StringComparison.OrdinalIgnoreCase))
            {
                if (found)
                    diagnostics.Add(new("multipleSymbolBlocks", lineNumber, "More than one symbols block is unsupported.", raw));
                else { found = true; inSymbols = true; }
                continue;
            }

            if (!inSymbols)
                continue;

            if (line.Equals("end", StringComparison.OrdinalIgnoreCase)) { inSymbols = false; ended = true; continue; }
            if (line.Length == 0) continue;

            declarations.Add(raw);
            if (ParseSymbol(line, raw, lineNumber, comment, diagnostics) is { } symbol)
            {
                symbols.Add(symbol);
                if (!names.Add(symbol.Name))
                    diagnostics.Add(new("duplicateSymbol", lineNumber, $"Duplicate symbol name '{symbol.Name}'.", raw));
            }
        }

        if (!found) diagnostics.Add(new("missingSymbolsBlock", null, "No symbols block was found."));
        else if (!ended) diagnostics.Add(new("missingSymbolsEnd", null, "The symbols block has no terminating end."));
        return new CogScript(name, text, symbols, found, ended, diagnostics, declarations);
    }

    /// <summary>
    /// `type name[=default] [local] [nolink] [desc=…] [linkid=…] [mask=…]`.
    /// Modifiers may also be comma-joined in one token (`nolink,desc=…`).
    /// </summary>
    private static CogSymbol? ParseSymbol(string line, string raw, int lineNumber, string? comment,
        List<CogSchemaDiagnostic> diagnostics)
    {
        void Problem(string code, string message) => diagnostics.Add(new(code, lineNumber, message, raw));
        var tokens = SplitOutsideQuotes(line, false, out bool balanced);
        if (!balanced) Problem("unsupportedDeclaration", "Unterminated quoted token in symbol declaration.");
        if (tokens.Count == 0) return null;

        var type = ParseType(tokens[0]);
        if (type == CogSymbolType.Unknown)
        {
            Problem("unknownDeclaration", $"Unknown symbol declaration type '{tokens[0]}'.");
            return null;
        }
        if (tokens.Count < 2)
        {
            Problem("unsupportedDeclaration", "Symbol declaration has no name.");
            return null;
        }

        var name = tokens[1];
        string? def = null;
        int eq = name.IndexOf('=');
        if (eq > 0)
        {
            def = name[(eq + 1)..];
            name = name[..eq];
        }
        if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            Problem("unsupportedDeclaration", $"Unsupported symbol name or declaration layout '{name}'.");
        if (eq >= 0 && string.IsNullOrEmpty(def))
            Problem("unsupportedDeclaration", "An explicit symbol default is empty.");

        bool local = false;
        string? description = null;
        bool noLink = false;
        int? explicitLink = null;
        uint? explicitMask = null;
        int effectiveLink = 0;
        var modifiers = new List<string>();

        for (int i = 2; i < tokens.Count; i++)
            foreach (var part in SplitOutsideQuotes(tokens[i], true, out _))
            {
                modifiers.Add(part);
                if (part.Equals("local", StringComparison.OrdinalIgnoreCase)) local = true;
                else if (part.StartsWith("desc=", StringComparison.OrdinalIgnoreCase)) description = part[5..];
                else if (part.Equals("nolink", StringComparison.OrdinalIgnoreCase)) { noLink = true; effectiveLink = -1; }
                else if (part.StartsWith("linkid=", StringComparison.OrdinalIgnoreCase))
                {
                    if (CogReferenceRules.TryIndex(part[7..], out int link)) { explicitLink = link; effectiveLink = link; }
                    else Problem("invalidModifier", "linkid must be a complete decimal 32-bit integer.");
                }
                else if (part.StartsWith("mask=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = part[5..];
                    if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
                    if (uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint mask)) explicitMask = mask;
                    else Problem("invalidModifier", "mask must be a complete hexadecimal 32-bit value.");
                }
                else Problem("unknownModifier", $"Unsupported symbol modifier '{part}'.");
            }

        return new CogSymbol(type, name, def, local, description ?? comment)
        {
            RawDeclaration = raw, LineNumber = lineNumber, RawModifiers = modifiers.AsReadOnly(),
            NoLink = noLink, ExplicitLinkId = explicitLink, ExplicitMask = explicitMask,
            EffectiveLinkId = effectiveLink, EffectiveMask = explicitMask ?? 0x401,
        };
    }

    private static List<string> SplitOutsideQuotes(string text, bool commas, out bool balanced)
    {
        var result = new List<string>();
        char quote = '\0';
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && (text[i] is '\'' or '"'))
            {
                if (quote == '\0') quote = text[i];
                else if (quote == text[i]) quote = '\0';
            }
            if (i == text.Length || quote == '\0' && (commas ? text[i] == ',' : char.IsWhiteSpace(text[i])))
            {
                if (i > start) result.Add(text[start..i]);
                start = i + 1;
            }
        }
        balanced = quote == '\0';
        return result;
    }

    private static CogSymbolType ParseType(string token) => token.ToLowerInvariant() switch
    {
        "message" => CogSymbolType.Message,
        "int" => CogSymbolType.Int,
        "flex" => CogSymbolType.Flex,
        "float" => CogSymbolType.Float,
        "vector" => CogSymbolType.Vector,
        "thing" => CogSymbolType.Thing,
        "sector" => CogSymbolType.Sector,
        "surface" => CogSymbolType.Surface,
        "sound" => CogSymbolType.Sound,
        "material" => CogSymbolType.Material,
        "model" => CogSymbolType.Model,
        "template" => CogSymbolType.Template,
        "keyframe" => CogSymbolType.Keyframe,
        "ai" => CogSymbolType.Ai,
        "cog" => CogSymbolType.Cog,
        _ => CogSymbolType.Unknown,
    };

    private static string StripComment(string line, out string? comment)
    {
        comment = null;
        int cut = line.Length;

        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] is '\'' or '"')
            {
                if (quote == '\0') quote = line[i];
                else if (quote == line[i]) quote = '\0';
            }
            if (quote == '\0' && (line[i] == '#' || line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/'))
            { cut = i; break; }
        }

        if (cut < line.Length)
        {
            var text = line[cut..].TrimStart('/', '#', ' ', '\t', '\r').Trim();
            if (text.Length > 0) comment = text;
        }

        return line[..cut];
    }
}

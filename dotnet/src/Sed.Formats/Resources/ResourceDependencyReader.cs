using System.Globalization;
using System.Text;
using Sed.Formats.Cogs;

namespace Sed.Formats.Resources;

/// <summary>A declared resource reference, with its original spelling and physical source line.</summary>
public sealed record ResourceDependency(string Name, string Folder, string Relation,
    int? LineNumber = null, string? SymbolName = null, string? Detail = null);

public sealed record ResourceDependencyDiagnostic(string Code, string Message, int? LineNumber = null);

/// <summary>Inspection of supported dependency fields, not validation of the whole resource.</summary>
/// <remarks>Malformed includes lexical barriers that prevent structural inspection under this reader's supported grammar.</remarks>
public enum ResourceDependencyReadState { Complete, Leaf, Unsupported, Malformed, LimitExceeded }

public sealed record ResourceDependencyReadResult(string Format, ResourceDependencyReadState State,
    IReadOnlyList<ResourceDependency> Dependencies,
    IReadOnlyList<ResourceDependencyDiagnostic> Diagnostics,
    IReadOnlyList<string> UnverifiedScopes)
{
    /// <summary>The same bounded, normalized complete schema for sibling format services; never serialized in reports.</summary>
    internal CogScript? CogSchema { get; init; }
}

public sealed record ResourceDependencyReadLimits(int MaxTextBytes = 4 * 1024 * 1024,
    int MaxLines = 100_000, int MaxLineChars = 16_384, int MaxReferences = 10_000);

/// <summary>
/// Reads only declared static dependency fields from captured resource bytes.
/// It performs no acquisition, archive lookup, installation discovery or script execution.
/// Folder selects the resource kind; filenames are never classified by their extensions.
///
/// Grammar references: OpenJKDF2 74e4657c, rdModel3_LoadEntry,
/// sithAnimClass_LoadPupEntry, sithSoundClass_LoadEntry, sithSprite_Load,
/// rdParticle_LoadEntry, sithAIClass_LoadEntry and sithCog_ParseSymbolRef.
/// The original SED LISTRES.PAS readers are additional format provenance.
/// This implementation is original and deliberately reports unsupported forms.
/// </summary>
public static class ResourceDependencyReader
{
    public const string Version = "sed-static-resource-dependencies-v1";

    // File-format vocabulary, including the MotS animation additions. Unknown
    // records remain explicit uncertainty instead of extension-based guesses.
    private static readonly HashSet<string> PuppetAnimations = Words(
        "stand walk run walkback strafeleft straferight death fire fire3 fire4 death2 hit hit2 " +
        "rising toss place drop fire2 fall land crouchforward crouchback activate magic choke leap " +
        "jump reserved block block2 turnleft turnright fidget fidget2 magic2 magic3 victory windup " +
        "holster drawfists drawgun drawsaber charge buttpunch");

    private static readonly HashSet<string> SoundEvents = Words(
        "create activate startmove stopmove moving lwalkhard rwalkhard lrunhard rrunhard " +
        "lwalkmetal rwalkmetal lrunmetal rrunmetal lwalkwater rwalkwater lrunwater rrunwater " +
        "lwalkpuddle rwalkpuddle lrunpuddle rrunpuddle lwalkearth rwalkearth lrunearth rrunearth " +
        "enterwater enterwaterslow exitwater exitwaterslow lswimsurface rswimsurface treadsurface " +
        "lswimunder rswimunder treadunder jump jumpmetal jumpwater jumpearth landhard landmetal " +
        "landwater landpuddle landearth landhurt hithard hitmetal hitearth deflected scrapehard " +
        "scrapemetal scrapeearth hitdamaged falling corpsehit hurtimpact hurtenergy hurtfire " +
        "hurtmagic hurtspecial drowning choking death1 death2 deathunder drowned splattered " +
        "pant breath gasp fire1 fire2 fire3 fire4 curious alert idle gloat fear boast happy victory " +
        "help flee search calm surprise reserved1 reserved2 reserved3 reserved4 reserved5 reserved6 " +
        "reserved7 reserved8");

    public static ResourceDependencyReadResult Read(string name, string folder,
        ReadOnlyMemory<byte> bytes, ResourceDependencyReadLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(folder);
        limits ??= new();
        if (limits.MaxTextBytes < 1 || limits.MaxLines < 1 || limits.MaxLineChars < 1 || limits.MaxReferences < 1)
            throw new ArgumentOutOfRangeException(nameof(limits), "Every dependency reader limit must be positive.");

        string format = folder.Replace('\\', '/').Trim('/').ToLowerInvariant() switch
        {
            "3do" => "3do", "misc/pup" => "pup", "misc/snd" => "snd",
            "misc/spr" => "spr", "misc/par" => "par", "cog" => "cog",
            "mat" => "mat", "misc/cmp" => "cmp", "sound" => "wav",
            "3do/key" => "key", "misc/ai" => "ai", _ => "unknown",
        };
        return new Reader(name, format, limits).Read(bytes);
    }

    private static HashSet<string> Words(string text) => new(text.Split(' '), StringComparer.OrdinalIgnoreCase);

    private sealed record SourceLine(int Number, string Text);
    private sealed record Argument(string Key, string Value, bool HasEquals);
    private sealed class ReferenceLimit(int lineNumber) : Exception
    {
        public int LineNumber { get; } = lineNumber;
    }

    private sealed class Reader(string name, string format, ResourceDependencyReadLimits limits)
    {
        private readonly List<ResourceDependency> _dependencies = new();
        private readonly List<ResourceDependencyDiagnostic> _diagnostics = new();
        private readonly List<string> _scopes = new()
        {
            "Dependency-field inspection does not establish full file validity or runtime loading.",
        };
        private ResourceDependencyReadState _state = ResourceDependencyReadState.Complete;
        private string _cogText = string.Empty;
        private CogScript? _cogSchema;

        public ResourceDependencyReadResult Read(ReadOnlyMemory<byte> bytes)
        {
            if (format == "unknown")
            {
                Problem(ResourceDependencyReadState.Unsupported, "unsupportedFormat",
                    "The declared resource folder has no supported dependency grammar.");
                return Result();
            }
            if (format is "mat" or "cmp" or "wav" or "key" or "ai")
            {
                _state = ResourceDependencyReadState.Leaf;
                if (format == "ai")
                    _scopes.Add("AI difficulty-specific variants and default.ai fallback are uninspected; only the explicitly captured resource is represented.");
                return Result();
            }
            if (format == "cog")
            {
                _scopes.Add("COG code, resource-loading calls and dynamically constructed resource names are not inspected.");
                _scopes.Add("Resource-valued positional COG parameters are not inspected; collected declarations are defaults, not effective placed-instance values.");
                _scopes.Add("COG template defaults and runtime template selection are in-level relations, not archive resource paths, and are not resolved by this reader.");
            }
            if (bytes.Length > limits.MaxTextBytes)
            {
                Problem(ResourceDependencyReadState.LimitExceeded, "textByteLimit",
                    $"Dependency text exceeds the {limits.MaxTextBytes}-byte inspection limit.");
                return Result();
            }

            // Resources are legacy byte text. Recognize an optional UTF-8 BOM
            // without replacing or silently discarding other non-ASCII bytes.
            var span = bytes.Span;
            if (span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) span = span[3..];
            string text = Encoding.Latin1.GetString(span);
            var lines = PrepareLines(text);
            if (lines is null) return Result();
            try
            {
                switch (format)
                {
                    case "3do": ReadModel(lines); break;
                    case "pup": ReadPuppet(lines); break;
                    case "snd": ReadSoundclass(lines); break;
                    case "spr": ReadSprite(lines); break;
                    case "par": ReadParticle(lines); break;
                    case "cog": ReadCog(); break;
                }
            }
            catch (ReferenceLimit limit)
            {
                Problem(ResourceDependencyReadState.LimitExceeded, "referenceLimit",
                    $"Dependency inspection stopped before exceeding {limits.MaxReferences} references.", limit.LineNumber);
            }
            return Result();
        }

        private ResourceDependencyReadResult Result() => new(format, _state,
            _dependencies.AsReadOnly(), _diagnostics.AsReadOnly(), _scopes.AsReadOnly())
        {
            CogSchema = _cogSchema,
        };

        private void Problem(ResourceDependencyReadState state, string code, string message, int? line = null)
        {
            // Enumeration order gives LimitExceeded > Malformed > Unsupported;
            // a later unsupported record cannot hide a structural failure.
            if ((int)state > (int)_state) _state = state;
            _diagnostics.Add(new(code, message, line));
        }

        private List<SourceLine>? PrepareLines(string text)
        {
            var lines = new List<SourceLine>();
            var cogText = format == "cog" ? new StringBuilder(text.Length) : null;
            using var reader = new StringReader(text);
            int number = 0;
            while (reader.ReadLine() is { } raw)
            {
                number++;
                if (number > limits.MaxLines)
                {
                    Problem(ResourceDependencyReadState.LimitExceeded, "lineLimit",
                        $"Dependency text exceeds the {limits.MaxLines}-physical-line inspection limit.", number);
                    return null;
                }
                if (raw.Length > limits.MaxLineChars)
                {
                    Problem(ResourceDependencyReadState.LimitExceeded, "lineLengthLimit",
                        $"A physical resource line exceeds the {limits.MaxLineChars}-character inspection limit.", number);
                    return null;
                }
                if (raw.Any(c => char.IsControl(c) && c != '\t'))
                {
                    Problem(ResourceDependencyReadState.Malformed, "binaryText",
                        "Dependency text contains unsupported control bytes; it was not interpreted as ordinary resource text.", number);
                    return null;
                }
                // stdConffile skips ';' comments only at physical column zero.
                bool semicolonComment = raw.StartsWith(';');
                cogText?.Append(semicolonComment ? string.Empty : raw).Append('\n');
                if (semicolonComment) continue;
                string content = (format == "cog" ? StripCogComment(raw) : StripHashComment(raw)).Trim();
                if (content.Length == 0) continue;
                if (content.StartsWith(';'))
                {
                    Problem(ResourceDependencyReadState.Malformed, "unsupportedSemicolonComment",
                        "An indented semicolon prevents validation under the supported resource grammar; dependency inspection is refused.", number);
                    return null;
                }
                if (content.EndsWith('\\'))
                {
                    Problem(ResourceDependencyReadState.Malformed, "unsupportedContinuation",
                        "A resource continuation prevents validation of supported dependency structure; inspection is refused.", number);
                    return null;
                }
                if (format != "cog" && (content.Contains('"') || content.Contains('\'')))
                {
                    Problem(ResourceDependencyReadState.Malformed, "unsupportedQuotedSyntax",
                        "A quoted resource record prevents validation under the supported dependency grammar; inspection is refused.", number);
                    return null;
                }
                lines.Add(new(number, content));
            }
            _cogText = cogText?.ToString() ?? string.Empty;
            return lines;
        }

        private void Add(string resource, string folder, string relation, SourceLine line,
            string? symbol = null, string? detail = null)
        {
            if (resource.Length > 1024)
            {
                Problem(ResourceDependencyReadState.Malformed, "resourceNameTooLong",
                    "The declared resource name exceeds the supported 1024-character inspection bound.", line.Number);
                return;
            }
            if (resource.Length == 0 || resource.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            {
                Problem(ResourceDependencyReadState.Malformed, "invalidResourceName",
                    "The resource field does not contain one nonempty literal token.", line.Number);
                return;
            }
            if (_dependencies.Count >= limits.MaxReferences) throw new ReferenceLimit(line.Number);
            _dependencies.Add(new(resource, folder, relation, line.Number, symbol, detail));
        }

        private void ReadModel(IReadOnlyList<SourceLine> lines)
        {
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!Section(lines[i], out string section) || !Same(section, "MODELRESOURCE")) continue;
                if (found)
                {
                    Problem(ResourceDependencyReadState.Malformed, "duplicateModelResource",
                        "More than one MODELRESOURCE section prevents an unambiguous material-table inspection.", lines[i].Number);
                    continue;
                }
                found = true;
                int sectionLine = lines[i].Number;
                if (++i >= lines.Count || Section(lines[i], out _) || Same(lines[i].Text, "END"))
                {
                    Problem(ResourceDependencyReadState.Malformed, "invalidMaterialCount",
                        "MODELRESOURCE has no MATERIALS count record.", sectionLine);
                    i--;
                    continue;
                }
                var header = Tokens(lines[i].Text);
                if (header.Length != 2 || !Same(header[0], "MATERIALS") || !Integer(header[1], out int count) || count is < 0 or > 65_536)
                {
                    Problem(ResourceDependencyReadState.Malformed, "invalidMaterialCount",
                        "MATERIALS must declare an integer count from 0 through 65536.", lines[i].Number);
                    continue;
                }
                i++;
                for (int row = 0; row < count; row++, i++)
                {
                    if (i >= lines.Count || Section(lines[i], out _) || Same(lines[i].Text, "END"))
                    {
                        Problem(ResourceDependencyReadState.Malformed, "truncatedMaterialTable",
                            $"The material table ends before its {count} declared rows are present.", i < lines.Count ? lines[i].Number : sectionLine);
                        break;
                    }
                    var line = lines[i];
                    int colon = line.Text.IndexOf(':');
                    string[] values = colon >= 0 ? Tokens(line.Text[(colon + 1)..]) : Array.Empty<string>();
                    if (colon <= 0 || !Integer(line.Text[..colon].Trim(), out _) || values.Length == 0)
                    {
                        Problem(ResourceDependencyReadState.Malformed, "invalidMaterialRow",
                            "A model material row requires an integer label, colon and resource name.", line.Number);
                        continue;
                    }
                    Add(values[0], "mat", "modelMaterial", line);
                }
                while (i < lines.Count && !Section(lines[i], out _) && !Same(lines[i].Text, "END"))
                {
                    Problem(ResourceDependencyReadState.Malformed, "extraMaterialData",
                        "Unexpected data follows the declared model material rows.", lines[i].Number);
                    i++;
                }
                i--;
            }
            if (!found)
                Problem(ResourceDependencyReadState.Malformed, "missingModelResource",
                    "No MODELRESOURCE material table was found; an empty dependency set is not asserted.");
        }

        private void ReadPuppet(IReadOnlyList<SourceLine> lines)
        {
            bool joints = false;
            int jointStart = 0;
            foreach (var line in lines)
            {
                var args = Arguments(line.Text);
                if (args.Length == 0) continue;
                if (joints)
                {
                    if (Same(args[0].Key, "end")) { joints = false; continue; }
                    if (args.Length != 1 || !args[0].HasEquals || !Integer(args[0].Key, out int part) || part is < 0 or > 9 || !Integer(args[0].Value, out _))
                        Problem(ResourceDependencyReadState.Malformed, "invalidPuppetJoint",
                            "A joints record requires one body-part index (0..9) and integer joint index joined by '='.", line.Number);
                    continue;
                }
                if (Same(args[0].Key, "mode"))
                {
                    if (!args[0].HasEquals || !NonnegativeInteger(args[0].Value) || args.Length > 2 ||
                        args.Length == 2 && (!Same(args[1].Key, "basedon") || !args[1].HasEquals || !NonnegativeInteger(args[1].Value)))
                        Problem(ResourceDependencyReadState.Malformed, "invalidPuppetMode",
                            "A mode record requires mode=<integer> and optionally basedon=<integer>.", line.Number);
                    continue;
                }
                if (Same(args[0].Value, "joints"))
                {
                    if (args.Length != 1)
                        Problem(ResourceDependencyReadState.Malformed, "invalidPuppetJoint", "The joints header has unsupported fields.", line.Number);
                    joints = true;
                    jointStart = line.Number;
                    continue;
                }
                if (args.Length == 1 && Same(args[0].Key, "end")) continue;
                if (!PuppetAnimations.Contains(args[0].Value))
                {
                    Problem(ResourceDependencyReadState.Unsupported, "unknownPuppetRecord",
                        $"Puppet record '{args[0].Value}' is not a supported animation or structural record.", line.Number);
                    continue;
                }
                if (args.Length is < 2 or > 5 || args.Length >= 3 && !Hex(args[2].Value) ||
                    args.Length >= 4 && !Integer(args[3].Value, out _) || args.Length == 5 && !Integer(args[4].Value, out _))
                {
                    Problem(ResourceDependencyReadState.Malformed, "invalidPuppetAnimation",
                        "An animation record requires a keyframe name followed only by optional hexadecimal flags and integer priorities.", line.Number);
                    continue;
                }
                if (!Same(args[1].Value, "none")) Add(args[1].Value, "3do/key", "puppetKeyframe", line);
            }
            if (joints)
                Problem(ResourceDependencyReadState.Malformed, "unterminatedPuppetJoints", "The joints block has no terminating end record.", jointStart);
        }

        private void ReadSoundclass(IReadOnlyList<SourceLine> lines)
        {
            foreach (var line in lines)
            {
                var args = Arguments(line.Text);
                if (args.Length == 0) continue;
                if (!SoundEvents.Contains(args[0].Value))
                {
                    Problem(ResourceDependencyReadState.Unsupported, "unknownSoundclassEvent",
                        $"Soundclass event '{args[0].Value}' is outside the supported event vocabulary.", line.Number);
                    continue;
                }
                if (args.Length is < 2 or > 6 || args.Length >= 3 && !Hex(args[2].Value) ||
                    args.Skip(3).Any(arg => !Finite(arg.Value, out _)))
                {
                    Problem(ResourceDependencyReadState.Malformed, "invalidSoundclassRecord",
                        "A soundclass row requires a sound name and optional hexadecimal flags, finite radii and volume.", line.Number);
                    continue;
                }
                if (!Same(args[1].Value, "none")) Add(args[1].Value, "sound", "soundclassSound", line);
            }
        }

        private void ReadSprite(IReadOnlyList<SourceLine> lines)
        {
            var line = lines.FirstOrDefault();
            var args = line is null ? Array.Empty<Argument>() : Arguments(line.Text);
            if (args.Length < 11 || !Integer(args[1].Value, out int type) || type is < 0 or > 2 ||
                !Finite(args[2].Value, out float width) || width <= 0 || !Finite(args[3].Value, out float height) || height <= 0 ||
                !Integer(args[4].Value, out _) || !Integer(args[5].Value, out _) || !Integer(args[6].Value, out _) ||
                args.Skip(7).Take(4).Any(arg => !Finite(arg.Value, out _)))
            {
                Problem(ResourceDependencyReadState.Malformed, "invalidSpriteRecord",
                    "The first sprite data record requires a material, type 0..2, positive finite dimensions, integer modes and finite light/offset values.", line?.Number);
                return;
            }
            Add(args[0].Value, "mat", "spriteMaterial", line!);
        }

        private void ReadParticle(IReadOnlyList<SourceLine> lines)
        {
            List<SourceLine>? header = null;
            int headerLine = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!Section(lines[i], out string section) || !Same(section, "HEADER")) continue;
                if (header is not null)
                {
                    Problem(ResourceDependencyReadState.Malformed, "duplicateParticleHeader", "More than one particle HEADER section is unsupported.", lines[i].Number);
                    continue;
                }
                headerLine = lines[i].Number;
                header = new();
                while (++i < lines.Count && !Section(lines[i], out _) && !Same(lines[i].Text, "END")) header.Add(lines[i]);
                i--;
            }
            if (header is null)
            {
                Problem(ResourceDependencyReadState.Malformed, "missingParticleHeader", "No particle HEADER section was found.");
                return;
            }
            var fields = header.Select(line => Tokens(line.Text)).ToArray();
            var materialRows = header.Where(line => Same(Tokens(line.Text)[0], "MATERIAL")).ToArray();
            if (materialRows.Length > 1)
                Problem(ResourceDependencyReadState.Malformed, "duplicateParticleMaterial", "More than one material is declared in the particle header.", materialRows[1].Number);
            if (fields.Length < 3 || fields[0].Length != 2 || !Same(fields[0][0], "PAR") || !VersionNumber(fields[0][1]) ||
                fields[1].Length != 2 || !Same(fields[1][0], "SIZE") || !Finite(fields[1][1], out _) ||
                fields[2].Length != 2 || !Same(fields[2][0], "MATERIAL"))
            {
                Problem(ResourceDependencyReadState.Malformed, "invalidParticleHeader",
                    "The particle header must declare PAR <major.minor>, SIZE <finite number> and MATERIAL <name> in order.", headerLine);
                return;
            }
            Add(fields[2][1], "mat", "particleMaterial", header[2]);
            for (int i = 3; i < fields.Length; i++)
            {
                if (Same(fields[i][0], "MATERIAL")) continue; // already diagnosed as a duplicate
                if (!Same(fields[i][0], "LIGHTINGMODE"))
                    Problem(ResourceDependencyReadState.Unsupported, "unknownParticleHeaderRecord", "An unrecognized particle header record was not interpreted as a filename.", header[i].Number);
                else if (fields[i].Length != 2 || !Integer(fields[i][1], out _))
                    Problem(ResourceDependencyReadState.Malformed, "invalidParticleHeader", "LIGHTINGMODE requires one integer value.", header[i].Number);
            }
        }

        private void ReadCog()
        {
            var script = CogScript.Parse(name, _cogText);
            if (!script.IsComplete)
            {
                foreach (var diagnostic in script.Diagnostics)
                    Problem(ResourceDependencyReadState.Malformed, "cogSchema." + diagnostic.Code, diagnostic.Message, diagnostic.LineNumber);
                return;
            }
            _cogSchema = script;
            foreach (var symbol in script.Symbols)
            {
                string? folder = symbol.Type switch
                {
                    CogSymbolType.Sound => "sound", CogSymbolType.Material => "mat", CogSymbolType.Model => "3do",
                    CogSymbolType.Keyframe => "3do/key", CogSymbolType.Ai => "misc/ai", _ => null,
                };
                if (folder is null || symbol.Default is not { } resource) continue;
                if (symbol.Type == CogSymbolType.Sound && Same(resource, "none")) continue;
                if (resource.Contains('"') || resource.Contains('\'') || resource.Any(char.IsWhiteSpace))
                {
                    Problem(ResourceDependencyReadState.Unsupported, "unsupportedResourceLiteral",
                        $"The declared resource default for '{symbol.Name}' is not an unquoted single token; it was not rewritten or inferred.", symbol.LineNumber);
                    continue;
                }
                Add(resource, folder, "cogDefault", new(symbol.LineNumber, symbol.RawDeclaration), symbol.Name,
                    symbol.Local ? "Declared script-local default." : "Declared external default; a placed value may override it.");
            }
        }

        private static bool Section(SourceLine line, out string section)
        {
            section = string.Empty;
            if (!line.Text.StartsWith("SECTION:", StringComparison.OrdinalIgnoreCase)) return false;
            section = line.Text[8..].Trim();
            return true;
        }

        private static string[] Tokens(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        private static Argument[] Arguments(string text) => text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(token =>
            {
                int equals = token.IndexOf('=');
                return equals < 0 ? new Argument(token, token, false) : new Argument(token[..equals], token[(equals + 1)..], true);
            }).ToArray();

        private static bool Same(string one, string two) => string.Equals(one, two, StringComparison.OrdinalIgnoreCase);

        private static bool Integer(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        private static bool NonnegativeInteger(string text) => Integer(text, out int number) && number >= 0;
        private static bool Finite(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

        private static bool Hex(string text)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _);
        }

        private static bool VersionNumber(string text)
        {
            var parts = text.Split('.');
            return parts.Length == 2 && NonnegativeInteger(parts[0]) && NonnegativeInteger(parts[1]);
        }

        private static string StripHashComment(string text)
        {
            int index = text.IndexOf('#');
            return index < 0 ? text : text[..index];
        }

        private static string StripCogComment(string text)
        {
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] is '\'' or '"')
                {
                    if (quote == '\0') quote = text[i];
                    else if (quote == text[i]) quote = '\0';
                }
                if (quote == '\0' && (text[i] == '#' || text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')) return text[..i];
            }
            return text;
        }
    }
}

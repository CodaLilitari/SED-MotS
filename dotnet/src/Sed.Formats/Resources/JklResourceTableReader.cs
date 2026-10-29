using System.Globalization;

namespace Sed.Formats.Resources;

/// <summary>Reads dependencies from retained standard JKL resource tables without resolving files.</summary>
public static class JklResourceTableReader
{
    private const int MaxLines = 1_000_000;
    private const int MaxLineChars = 16_384;

    private sealed record Table(string CountNoun, string Folder, string Relation,
        bool BareNames = false, bool HasNullName = false, bool TemplateNames = false);

    // SAVEJKL.INC's WriteListSection writes a buffer capacity followed by the
    // actual indexed rows. Its SOUNDS writer instead emits bare filenames.
    // Native sith* list loaders consume row order; the numeric labels are not
    // allocation indices and need not be dense or unique.
    private static readonly IReadOnlyDictionary<string, Table> Tables = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase)
    {
        ["SOUNDS"] = new("sounds", "sound", "jklSound", BareNames: true, HasNullName: true),
        ["MODELS"] = new("models", "3do", "jklModel"),
        ["SPRITES"] = new("sprites", "misc/spr", "jklSprite"),
        ["KEYFRAMES"] = new("keyframes", "3do/key", "jklKeyframe"),
        ["ANIMCLASS"] = new("puppets", "misc/pup", "jklPuppet"),
        ["SOUNDCLASS"] = new("soundclasses", "misc/snd", "jklSoundclass", HasNullName: true),
        ["AICLASS"] = new("aiclasses", "misc/ai", "jklAiClass"),
        ["COGSCRIPTS"] = new("scripts", "cog", "jklCogScript"),
        ["TEMPLATES"] = new("templates", "", "", TemplateNames: true),
    };

    private static readonly HashSet<string> ModeledSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "COPYRIGHT", "HEADER", "MATERIALS", "GEORESOURCE", "SECTORS", "THINGS",
        "LIGHTS", "LAYERS", "COGS", "ARCHLIGHTING", "SED_SOURCE_LIGHTS", "SED_ARCH_AUTHORING",
    };

    public static ResourceDependencyReadResult Read(IReadOnlyList<string> sourceLines, int maxReferences = 16_384)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        ArgumentOutOfRangeException.ThrowIfNegative(maxReferences);

        var dependencies = new List<ResourceDependency>();
        var diagnostics = new List<ResourceDependencyDiagnostic>();
        var unverifiedScopes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ResourceDependencyReadResult Result(ResourceDependencyReadState state) =>
            new("jklResourceTables", state, dependencies.ToArray(), diagnostics.ToArray(), unverifiedScopes.ToArray());

        ResourceDependencyReadResult Failure(ResourceDependencyReadState state, string code, string message, int? line)
        {
            diagnostics.Add(new(code, message, line));
            return Result(state);
        }

        void Disclose(string code, string message, int line)
        {
            diagnostics.Add(new(code, message, line));
            unverifiedScopes.Add($"{message} (line {line})");
        }

        // Validate the physical input before scanning even ignored sections or
        // appending a dependency. The caller also bounds the source byte count.
        if (sourceLines.Count > MaxLines)
            return Failure(ResourceDependencyReadState.LimitExceeded, "jklLineCountLimit",
                $"JKL source exceeds the {MaxLines} line limit.", null);
        for (int line = 0; line < sourceLines.Count; line++)
            if (sourceLines[line].Length > MaxLineChars)
                return Failure(ResourceDependencyReadState.LimitExceeded, "jklLineLimit",
                    $"JKL source line exceeds the {MaxLineChars} character limit.", line + 1);

        int cursor = 0;
        while (cursor < sourceLines.Count)
        {
            ReadOnlySpan<char> header = Content(sourceLines[cursor]);
            if (header.IsEmpty) { cursor++; continue; }

            int sectionStart = cursor++;
            while (cursor < sourceLines.Count && !IsSection(Content(sourceLines[cursor]))) cursor++;
            int sectionEnd = cursor;

            if (!IsSection(header))
            {
                Disclose("unsectionedJklData", "Unsectioned JKL records were not inspected as resource tables.", sectionStart + 1);
                continue;
            }

            string section = header[8..].Trim().ToString();
            if (section.Length == 0 || section.Any(char.IsWhiteSpace))
                return Failure(ResourceDependencyReadState.Malformed, "invalidJklSectionHeader",
                    "A JKL section header must contain one section name.", sectionStart + 1);

            if (ModeledSections.Contains(section) || Tables.ContainsKey(section) ||
                section.Equals("JK", StringComparison.OrdinalIgnoreCase))
            {
                // stdConffile_ReadLine recognizes semicolon comments only at
                // physical column zero. Trimming first would hide data rows
                // such as `  ; hidden.3do` from the dependency inventory.
                for (int line = sectionStart + 1; line < sectionEnd; line++)
                {
                    if (HasIndentedSemicolon(sourceLines[line]))
                        return Failure(ResourceDependencyReadState.Malformed, "unsupportedSemicolonComment",
                            $"Unsupported source/editor semantics: the engine reads an indented semicolon record in {section} as data.", line + 1);

                    bool templates = section.Equals("TEMPLATES", StringComparison.OrdinalIgnoreCase);
                    if (!templates && !section.Equals("THINGS", StringComparison.OrdinalIgnoreCase)) continue;
                    ReadOnlySpan<char> record = Content(sourceLines[line]);
                    if (templates && (sourceLines[line].StartsWith(';') || record.StartsWith("//", StringComparison.Ordinal)))
                        return Failure(ResourceDependencyReadState.Malformed, "unsupportedTemplateComment",
                            "Unsupported source/editor semantics: TEMPLATES comment-like rows are skipped or counted differently by the editor and engine.", line + 1);
                    if (record.Contains(','))
                        return Failure(ResourceDependencyReadState.Malformed, "unsupportedModeledComma",
                            $"Unsupported source/editor semantics: comma-separated records in {section} are tokenized differently by the editor and engine.", line + 1);
                    if (record.EndsWith("\\", StringComparison.Ordinal))
                        return Failure(ResourceDependencyReadState.Malformed,
                            templates ? "unsupportedTemplateContinuation" : "unsupportedThingContinuation",
                            $"Unsupported source/editor semantics: continued {section} records cannot be bound safely to the modeled declarations.", line + 1);
                }
            }

            if (ModeledSections.Contains(section)) continue;
            if (section.Equals("JK", StringComparison.OrdinalIgnoreCase))
            {
                // SAVEJKL.INC emits this empty format marker before COPYRIGHT.
                for (int line = sectionStart + 1; line < sectionEnd; line++)
                {
                    if (Content(sourceLines[line]).IsEmpty) continue;
                    Disclose("nonemptyJklMarker", "The JK format marker contains unmodeled records.", line + 1);
                    break;
                }
                continue;
            }
            if (!Tables.TryGetValue(section, out var table))
            {
                Disclose("unknownJklSection", $"JKL section {section} has no reviewed resource-table reader.", sectionStart + 1);
                continue;
            }
            if (!seen.Add(section))
                return Failure(ResourceDependencyReadState.Malformed, "duplicateResourceTable",
                    $"JKL resource table {section} occurs more than once.", sectionStart + 1);

            int rowLine = sectionStart + 1;
            while (rowLine < sectionEnd && Content(sourceLines[rowLine]).IsEmpty) rowLine++;
            if (rowLine == sectionEnd || Content(sourceLines[rowLine]).Equals("end", StringComparison.OrdinalIgnoreCase))
                return Failure(ResourceDependencyReadState.Malformed, "missingResourceTableHeader",
                    $"JKL resource table {section} is missing its World {table.CountNoun} capacity header.",
                    rowLine < sectionEnd ? rowLine + 1 : sectionStart + 1);

            var countFields = Arguments(Content(sourceLines[rowLine]));
            if (countFields.Length != 3 || !countFields[0].Equals("world", StringComparison.OrdinalIgnoreCase) ||
                !countFields[1].Equals(table.CountNoun, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(countFields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int capacity) || capacity < 0)
                return Failure(ResourceDependencyReadState.Malformed, "invalidResourceTableHeader",
                    $"JKL resource table {section} requires World {table.CountNoun} and a nonnegative integer capacity.", rowLine + 1);

            // Validate the known header and table boundaries before disclosing
            // unsupported row syntax. A continuation must not downgrade a
            // concrete malformed header or missing/extra terminator to a scope
            // that could later be acknowledged as merely unverified.
            int continuation = -1;
            int terminator = -1;
            for (int line = rowLine + 1; line < sectionEnd; line++)
            {
                ReadOnlySpan<char> row = Content(sourceLines[line]);
                if (row.IsEmpty) continue;
                if (terminator >= 0)
                    return Failure(ResourceDependencyReadState.Malformed, "extraResourceTableData",
                        $"JKL resource table {section} has records after its end terminator.", line + 1);

                var fields = Arguments(row);
                if (fields.Length > 0 && fields[0].Equals("end", StringComparison.OrdinalIgnoreCase))
                {
                    if (fields.Length != 1)
                        return Failure(ResourceDependencyReadState.Malformed, "invalidResourceTableRow",
                            $"JKL resource table {section} has extra fields on its end terminator.", line + 1);
                    terminator = line;
                    continue;
                }
                if (continuation < 0 && row.EndsWith("\\", StringComparison.Ordinal)) continuation = line;
            }
            if (terminator < 0)
                return Failure(ResourceDependencyReadState.Malformed, "unterminatedResourceTable",
                    $"JKL resource table {section} is missing its end terminator.", sectionStart + 1);
            if (continuation >= 0)
            {
                if (table.TemplateNames)
                    return Failure(ResourceDependencyReadState.Malformed, "unsupportedTemplateContinuation",
                        "Unsupported source semantics: continued TEMPLATES rows cannot be checked safely against the modeled template declarations.",
                        continuation + 1);
                // stdConffile_ReadLine joins physical lines ending in a
                // backslash. Retain physical provenance and disclose that
                // syntax instead of inventing an incomplete resource name.
                Disclose("continuedResourceTable", $"Line continuation in JKL resource table {section} was not inspected.", continuation + 1);
                continue;
            }

            int actualRows = 0;
            for (rowLine++; rowLine < terminator; rowLine++)
            {
                ReadOnlySpan<char> row = Content(sourceLines[rowLine]);
                if (row.IsEmpty) continue;
                var fields = Arguments(row);
                string? name = null;
                if (table.TemplateNames)
                {
                    if (fields.Length >= 2 && fields[1].Length > 0) name = fields[0];
                }
                else if (table.BareNames)
                {
                    // sithSound_ReadSoundsListText uses the first argument as
                    // the filename; additional arguments only select preload.
                    if (fields.Length > 0 && !fields[0].EndsWith(':')) name = fields[0];
                }
                else if (fields.Length >= 2 && fields[0].EndsWith(':') &&
                    int.TryParse(fields[0].AsSpan(0, fields[0].Length - 1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int label) && label >= 0)
                {
                    name = fields[1];
                }
                if (string.IsNullOrEmpty(name))
                    return Failure(ResourceDependencyReadState.Malformed, "invalidResourceTableRow",
                        $"JKL resource table {section} contains an unreadable {(table.TemplateNames ? "name and parent" : table.BareNames ? "filename" : "indexed filename")} row.", rowLine + 1);
                // sithTemplate_Parse reuses the first existing template name,
                // while the current modeled dictionary retains the last. The
                // untouched raw source must therefore refuse duplicate names;
                // parameter dependency extraction remains the model's job.
                if (table.TemplateNames && !templateNames.Add(name))
                    return Failure(ResourceDependencyReadState.Malformed, "duplicateTemplateName",
                        $"JKL template {name} is declared more than once.", rowLine + 1);
                if (actualRows >= capacity)
                    return Failure(ResourceDependencyReadState.LimitExceeded, "resourceTableCapacityExceeded",
                        $"JKL resource table {section} contains more rows than its declared capacity.", rowLine + 1);
                actualRows++;
                if (table.TemplateNames) continue;

                // Only these two null rules are verified: sithSound_Load and
                // sithSoundClass_ReadSoundClassesListText explicitly skip none.
                // Do not extend that sentinel to the other resource tables.
                if (table.HasNullName && name.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
                if (dependencies.Count >= maxReferences)
                    return Failure(ResourceDependencyReadState.LimitExceeded, "resourceReferenceLimit",
                        $"JKL resource tables exceed the {maxReferences} reference limit.", rowLine + 1);
                dependencies.Add(new(name, table.Folder, table.Relation, rowLine + 1));
            }
        }

        return Result(unverifiedScopes.Count == 0 ? ResourceDependencyReadState.Complete : ResourceDependencyReadState.Unsupported);
    }

    private static ReadOnlySpan<char> Content(string sourceLine)
    {
        ReadOnlySpan<char> line = sourceLine.AsSpan();
        int comment = line.IndexOf('#');
        if (comment >= 0) line = line[..comment];
        line = line.Trim();
        return line.IsEmpty || line[0] == ';' ? ReadOnlySpan<char>.Empty : line;
    }

    private static bool IsSection(ReadOnlySpan<char> line) => line.StartsWith("SECTION:", StringComparison.OrdinalIgnoreCase);

    private static bool HasIndentedSemicolon(string sourceLine)
    {
        if (sourceLine.Length == 0 || !char.IsWhiteSpace(sourceLine[0])) return false;
        return sourceLine.AsSpan().TrimStart().StartsWith(";", StringComparison.Ordinal);
    }

    private static string[] Arguments(ReadOnlySpan<char> line)
    {
        // stdConffile_ReadArgsFromStr separates commas/ASCII whitespace and
        // consumers read the value after the first '=' rather than the key.
        string[] fields = line.ToString().Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < fields.Length; i++)
        {
            int equals = fields[i].IndexOf('=');
            if (equals >= 0) fields[i] = fields[i][(equals + 1)..];
        }
        return fields;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sed.Core.Lighting;
using Sed.Core.Model;

namespace Sed.Formats.Jkl;

/// <summary>Current indexed ARCH consistency, distinct from recorded bake dependency freshness.</summary>
internal static class ArchitectureAuthoringMetadataFormat
{
    internal const string SectionName = "SED_ARCH_AUTHORING";
    internal sealed record Row(int ThingIndex, int ArchitectureIndex, ArchitectureLightingProvenance Provenance);
    internal sealed record Metadata(string BaseHash, IReadOnlyList<Row> Rows);

    internal static bool Needed(Level level) => level.Things.Any(thing => thing.ArchLightingProvenance is not null);

    internal static List<string> Build(Level level)
    {
        var rows = new List<Row>();
        for (int i = 0; i < level.Things.Count; i++)
            if (level.Things[i].ArchLightingProvenance is { } provenance)
            {
                ValidateProvenance(provenance);
                ValidateBinding(level, i, level.Things[i].ArchLightingIndex, provenance);
                rows.Add(new(i, level.Things[i].ArchLightingIndex, provenance));
            }
        var lines = new List<string> { "SECTION: " + SectionName, "VERSION 1", "BINDINGS_SHA256 " + BaseHash(level), "BINDINGS " + rows.Count };
        foreach (var row in rows)
        {
            var p = row.Provenance;
            lines.Add(FormattableString.Invariant($"{row.ThingIndex}: {row.ArchitectureIndex} {p.Origin} {p.ModelName} {p.ModelSha256} {(p.InputSha256.Length == 0 ? "none" : p.InputSha256)} {p.SamplesSha256}"));
        }
        lines.Add("END");
        return lines;
    }

    internal static Metadata Read(JklReader reader)
    {
        string[] Fields(int count, string? key = null)
        {
            if (!reader.Next(upper: false) || reader.EndOfSection) throw new FormatException("Truncated architectural authoring metadata.");
            string[] fields = JklReader.Tokens(reader.Current);
            if (fields.Length != count || key is not null && !fields[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Unexpected architectural authoring metadata field.");
            return fields;
        }
        if (Fields(2, "VERSION")[1] != "1") throw new NotSupportedException("Unsupported architectural authoring metadata version.");
        string hash = Fields(2, "BINDINGS_SHA256")[1]; ValidateHash(hash);
        int count = JklReader.ParseInt(Fields(2, "BINDINGS")[1]);
        if (count is < 0 or > 1_000_000) throw new FormatException("Architectural authoring metadata count exceeds the supported bound.");
        var rows = new List<Row>(); int last = -1;
        for (int i = 0; i < count; i++)
        {
            var fields = Fields(7);
            if (!fields[0].EndsWith(':') || !int.TryParse(fields[0][..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int thing) || thing <= last)
                throw new FormatException("Architectural authoring thing indices must be unique and strictly increasing.");
            int architecture = JklReader.ParseInt(fields[1]);
            if (architecture < 0) throw new FormatException("Architectural authoring requires a bound ARCH object.");
            var provenance = new ArchitectureLightingProvenance(fields[2], fields[3], fields[4].ToLowerInvariant(),
                fields[5] == "none" ? string.Empty : fields[5].ToLowerInvariant(), fields[6].ToLowerInvariant());
            ValidateProvenance(provenance);
            rows.Add(new(thing, architecture, provenance)); last = thing;
        }
        if (!reader.Next(upper: false) || !reader.Current.Equals("END", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Architectural authoring metadata has extra records or is missing END.");
        if (reader.Next(upper: false))
        {
            var next = JklReader.Tokens(reader.Current);
            if (next.Length != 2 || !next[0].Equals("SECTION:", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Unexpected content after architectural authoring metadata END.");
        }
        return new(hash.ToLowerInvariant(), rows.AsReadOnly());
    }

    internal static void Apply(Level level, Metadata metadata)
    {
        if (metadata.BaseHash != BaseHash(level))
            throw new FormatException("Architectural authoring metadata no longer matches the ordinary thing/model bindings and ARCH samples.");
        foreach (var row in metadata.Rows) ValidateBinding(level, row.ThingIndex, row.ArchitectureIndex, row.Provenance);
        // Only apply after all records pass. Stale input/sample digests are
        // retained evidence, not a reason to replace them with fresh digests.
        foreach (var row in metadata.Rows) level.Things[row.ThingIndex].ArchLightingProvenance = row.Provenance;
    }

    private static void ValidateBinding(Level level, int thingIndex, int architectureIndex, ArchitectureLightingProvenance provenance)
    {
        if (level.Kind != ProjectType.MysteriesOfTheSith || (uint)thingIndex >= level.Things.Count ||
            (uint)architectureIndex >= level.ArchitecturalLighting.Count || level.Things[thingIndex].ArchLightingIndex != architectureIndex)
            throw new FormatException("Architectural authoring metadata references a missing or different thing/ARCH binding.");
        if (!level.GetThingModel(level.Things[thingIndex]).Equals(provenance.ModelName, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Architectural authoring model name differs from the current resolved model binding.");
    }

    private static void ValidateProvenance(ArchitectureLightingProvenance value)
    {
        if (value.Origin is not ("bound" or "reset" or ArchitecturalLightingAuthoring.BakeProfile))
            throw new FormatException("Unsupported architectural authoring origin/profile.");
        if (string.IsNullOrEmpty(value.ModelName) || value.ModelName.Length > 127 ||
            value.ModelName.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '#'))
            throw new FormatException("Architectural authoring model name must be one bounded token.");
        ValidateHash(value.ModelSha256); ValidateHash(value.SamplesSha256);
        if (value.Origin == ArchitecturalLightingAuthoring.BakeProfile) ValidateHash(value.InputSha256);
        else if (value.InputSha256 != string.Empty) throw new FormatException("Unbaked architectural authoring origins cannot assert a bake input digest.");
    }

    private static void ValidateHash(string text)
    {
        if (text is null || text.Length != 64 || text.Any(c => !Uri.IsHexDigit(c)))
            throw new FormatException("Architectural authoring digests must be SHA256 values.");
    }

    // Published v1 canonical form: BinaryWriter UTF8 length-prefixed strings and
    // little-endian Int32 counts/indices. Model names are invariant lowercase.
    // ARCH payload uses its canonical finite round-trip text, LF, no final LF.
    internal static string BaseHash(Level level)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("SED-ARCH-AUTHORING-BASE-1"); writer.Write((int)level.Kind);
            writer.Write(level.Things.Count);
            foreach (var thing in level.Things)
            {
                writer.Write(thing.ArchLightingIndex);
                writer.Write(level.GetThingModel(thing).ToLowerInvariant());
            }
            writer.Write(string.Join('\n', ArchitecturalLightingFormat.Build(level.ArchitecturalLighting)));
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }

    internal static string? SourceTextHash(string[] source)
    {
        var selected = new List<string>(); bool include = false, present = false;
        foreach (string raw in source)
        {
            var fields = JklReader.Tokens(raw.Split('#')[0].Trim());
            if (fields.Length > 0 && fields[0].Equals("SECTION:", StringComparison.OrdinalIgnoreCase))
            {
                include = fields.Length >= 2 && fields[1].Equals(SectionName, StringComparison.OrdinalIgnoreCase);
                present |= include;
                if (include && fields.Length != 2) throw new FormatException("Architectural authoring section header must contain exactly one section name.");
            }
            if (include) selected.Add(raw);
        }
        return present ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', selected)))) : null;
    }

    internal static void ValidateRetainedSource(JklDocument document)
    {
        string? current = SourceTextHash(document.SourceLines);
        if (current is not null && (document.OriginalArchitectureAuthoringTextHash != current ||
            !document.OriginalSections.ContainsKey(SectionName)))
            throw new InvalidOperationException("Architectural authoring metadata requires a validated unchanged parsed source baseline. Reopen the source before export.");
        if (document.OriginalArchitectureAuthoringTextHash is not null && current != document.OriginalArchitectureAuthoringTextHash)
            throw new InvalidOperationException("Retained architectural authoring metadata changed or disappeared outside the document model. Reopen the source before export.");
    }
}

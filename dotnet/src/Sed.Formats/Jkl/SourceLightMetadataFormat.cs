using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Formats.Jkl;

/// <summary>
/// Additional editor source-light state. The ordinary LIGHTS rows retain their
/// established shape; a semantic checksum refuses stale metadata after another
/// editor changes or reorders those rows. No engine lighting section is extended.
/// </summary>
internal static class SourceLightMetadataFormat
{
    internal const string SectionName = "SED_SOURCE_LIGHTS";
    internal sealed record Row(double? RgbIntensity, double? LegacyRgbRange, ColorF Color);
    internal sealed record Metadata(string BaseHash, IReadOnlyList<Row> Rows);

    internal static bool Needed(Level level) => level.Lights.Any(light =>
        light.RgbIntensity.HasValue || light.LegacyRgbRange.HasValue ||
        level.Kind == ProjectType.JediKnight && light.Color != ColorF.White);

    internal static List<string> Build(Level level)
    {
        var lines = new List<string> { "SECTION: " + SectionName, "VERSION 1",
            "LIGHTS_SHA256 " + BaseHash(level, projectedOutput: true), "SOURCE_LIGHTS " + level.Lights.Count };
        for (int i = 0; i < level.Lights.Count; i++)
        {
            var light = level.Lights[i];
            lines.Add($"{i}: {Optional(light.RgbIntensity, "inherit")} {Optional(light.LegacyRgbRange, "none")} " +
                $"{Number(light.Color.R)} {Number(light.Color.G)} {Number(light.Color.B)}");
        }
        lines.Add("END");
        return lines;
    }

    internal static Metadata Read(JklReader reader)
    {
        string[] Fields(int count, string? key = null)
        {
            if (!reader.Next(upper: false) || reader.EndOfSection)
                throw new FormatException("Truncated source-light metadata.");
            var fields = JklReader.Tokens(reader.Current);
            if (fields.Length != count || key is not null && !fields[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Unexpected or unsupported source-light metadata field.");
            return fields;
        }
        var version = Fields(2, "VERSION");
        if (version[1] != "1") throw new NotSupportedException("Unsupported source-light metadata version; no partial import is allowed.");
        string hash = Fields(2, "LIGHTS_SHA256")[1];
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new FormatException("Invalid source-light LIGHTS_SHA256.");
        int count = JklReader.ParseInt(Fields(2, "SOURCE_LIGHTS")[1]);
        if (count is < 0 or > 1_000_000) throw new FormatException("Source-light metadata count exceeds the supported bound.");
        var rows = new List<Row>();
        for (int i = 0; i < count; i++)
        {
            var fields = Fields(6);
            if (fields[0] != i.ToString(CultureInfo.InvariantCulture) + ":")
                throw new FormatException("Source-light metadata indices must be unique and sequential.");
            rows.Add(new(ParseOptional(fields[1], "inherit"), ParseOptional(fields[2], "none"),
                new ColorF(Single(fields[3]), Single(fields[4]), Single(fields[5]))));
        }
        if (!reader.Next(upper: false) || !reader.Current.Equals("END", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Source-light metadata has extra fields or is missing END.");
        if (reader.Next(upper: false))
        {
            var next = JklReader.Tokens(reader.Current);
            if (next.Length != 2 || !next[0].Equals("SECTION:", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Unexpected source-light metadata content after END; expected another SECTION or end of file.");
        }
        return new(hash.ToLowerInvariant(), rows.AsReadOnly());
    }

    internal static void Apply(Level level, Metadata metadata, IReadOnlySet<Light> rowsWithColor)
    {
        if (metadata.Rows.Count != level.Lights.Count || metadata.BaseHash != BaseHash(level, projectedOutput: false))
            throw new FormatException("Source-light metadata no longer matches the ordinary LIGHTS rows. Reconcile the source-light state before opening or exporting this file.");
        for (int i = 0; i < metadata.Rows.Count; i++)
        {
            var row = metadata.Rows[i];
            if (rowsWithColor.Contains(level.Lights[i]) && !SameColorBits(row.Color, level.Lights[i].Color))
                throw new FormatException("Source-light metadata color differs from the RGB color already stored in its ordinary LIGHTS row.");
            level.Lights[i].RgbIntensity = row.RgbIntensity;
            level.Lights[i].LegacyRgbRange = row.LegacyRgbRange;
            level.Lights[i].Color = row.Color;
        }
    }

    internal static string BaseHash(Level level, bool projectedOutput)
    {
        var text = new StringBuilder("sed-source-light-base-v1\n");
        text.Append(level.Lights.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var light in level.Lights)
        {
            var color = projectedOutput && level.Kind == ProjectType.JediKnight ? ColorF.White : light.Color;
            text.Append(light.Flags.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(light.Layer.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(Number(light.Position.X)).Append(' ').Append(Number(light.Position.Y)).Append(' ').Append(Number(light.Position.Z)).Append(' ')
                .Append(Number(light.Range)).Append(' ').Append(Number(light.Intensity)).Append(' ')
                .Append(Number(color.R)).Append(' ').Append(Number(color.G)).Append(' ').Append(Number(color.B)).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    // SourceLines is publicly accessible. Even an unchanged-section copy must
    // not bypass validation of unknown, stale or subsequently altered metadata.
    internal static void ValidateRetainedSource(JklDocument document)
    {
        var (selected, metadataPresent) = SelectSourceSections(document.SourceLines);
        if (metadataPresent) JklParser.Parse(string.Join('\n', selected));
        if (document.OriginalSections.ContainsKey(SectionName) && document.OriginalSourceLightTextHash != TextHash(selected, metadataPresent))
            throw new InvalidOperationException("Source-light text changed outside the document model. Reopen the changed source before exporting it.");
    }

    internal static string? SourceTextHash(string[] sourceLines)
    {
        var (selected, metadataPresent) = SelectSourceSections(sourceLines);
        return TextHash(selected, metadataPresent);
    }

    private static string? TextHash(List<string> selected, bool metadataPresent) => metadataPresent
        ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', selected)))) : null;

    private static (List<string> selected, bool metadataPresent) SelectSourceSections(string[] sourceLines)
    {
        var selected = new List<string>();
        bool include = false, metadataPresent = false;
        foreach (string raw in sourceLines)
        {
            var fields = JklReader.Tokens(raw.Split('#')[0].Trim());
            if (fields.Length > 0 && fields[0].Equals("SECTION:", StringComparison.OrdinalIgnoreCase))
            {
                include = fields.Length >= 2 && (fields[1].Equals("LIGHTS", StringComparison.OrdinalIgnoreCase) ||
                    fields[1].Equals(SectionName, StringComparison.OrdinalIgnoreCase));
                metadataPresent |= fields.Length >= 2 && fields[1].Equals(SectionName, StringComparison.OrdinalIgnoreCase);
            }
            if (include) selected.Add(raw);
        }
        return (selected, metadataPresent);
    }

    private static string Optional(double? value, string missing) => value.HasValue ? Number(value.Value) : missing;
    private static bool SameColorBits(ColorF a, ColorF b) =>
        BitConverter.SingleToInt32Bits(a.R) == BitConverter.SingleToInt32Bits(b.R) &&
        BitConverter.SingleToInt32Bits(a.G) == BitConverter.SingleToInt32Bits(b.G) &&
        BitConverter.SingleToInt32Bits(a.B) == BitConverter.SingleToInt32Bits(b.B);
    private static double? ParseOptional(string text, string missing) => text.Equals(missing, StringComparison.OrdinalIgnoreCase) ? null : JklReader.ParseFloat(text);
    private static float Single(string text)
    {
        float value = (float)JklReader.ParseFloat(text);
        return float.IsFinite(value) ? value : throw new FormatException("Source-light color exceeds the supported finite float range.");
    }
    private static string Number(double value) => double.IsFinite(value)
        ? value.ToString("R", CultureInfo.InvariantCulture) : throw new InvalidOperationException("Source-light values must be finite before export.");
}

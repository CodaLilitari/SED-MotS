using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Sed.Core.Lighting;
using Sed.Core.Model;
using Sed.Core.Validation;

namespace Sed.Formats.Jkl;

public sealed record SurfaceVertexColorReadiness(int GlobalSurfaceIndex, int SectorIndex, int SurfaceIndex,
    int CornerCount, string Layout, string Retail, string OpenMots, int? DistinguishingCorner,
    int GeometryMode, int LightingMode, bool Adjoined, string Reason);
public sealed record DynamicVertexColorReport(string Status, string? ProposedOutputSha256,
    bool? GeometryValid, IReadOnlyList<SurfaceVertexColorReadiness> Surfaces, string Detail)
{
    public object Summary() => new
    {
        Status, ProposedOutputSha256, GeometryValid, surfaceCount = Surfaces.Count,
        retailRetained = Surfaces.Count(s => s.Retail == nameof(VertexRgbStorage.Retained)),
        retailCompacted = Surfaces.Count(s => s.Retail == nameof(VertexRgbStorage.Compacted)),
        retailUnknown = Surfaces.Count(s => s.Retail == nameof(VertexRgbStorage.Unknown)),
        openMotsScalar = Surfaces.Count(s => s.OpenMots == nameof(VertexRgbStorage.Scalar)), Detail,
    };
}

/// <summary>Examines actual candidate bytes, including unchanged source rows retained by JklWriter.</summary>
public static class JklDynamicVertexColors
{
    public const string Scope = "Static loader-storage inference for proposed serialized output. Visibility, COG execution, networking and retail runtime parity are not tested. Flags and fullbright/hidden/adjoin modes do not promote RGB storage. No values are changed.";

    public static DynamicVertexColorReport Evaluate(JklDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Level.Kind != ProjectType.MysteriesOfTheSith)
            return new("unsupported-game", null, null, [], "This diagnostic is scoped to MotS. " + Scope);
        try
        {
            var geometryIssue = ConsistencyChecker.Check(document.Level).FirstOrDefault(i => i.Severity == IssueSeverity.Error
                && i.Type is ItemType.Sector or ItemType.Surface);
            if (geometryIssue is not null)
                return new("unavailable", null, false, [], "Invalid geometry prevents proposed-output assessment: " + geometryIssue.Message);
            return EvaluateSerialized(JklWriter.Build(document));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or NotSupportedException or OverflowException)
        { return new("unavailable", null, null, [], "Proposed serialization could not be assessed: " + e.Message); }
    }

    public static DynamicVertexColorReport EvaluateSerialized(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output)));
        try
        {
            var reopened = JklParser.ParseDocument(output);
            if (reopened.Level.Kind != ProjectType.MysteriesOfTheSith)
                return new("unsupported-game", hash, null, [], "This diagnostic is scoped to MotS. " + Scope);
            var geometryIssue = ConsistencyChecker.Check(reopened.Level).FirstOrDefault(i => i.Severity == IssueSeverity.Error
                && i.Type is ItemType.Sector or ItemType.Surface);
            if (geometryIssue is not null)
                return new("unavailable", hash, false, [], "Invalid serialized geometry prevents assessment: " + geometryIssue.Message);
            var ordered = GeoResourceWriter.OrderedSurfaces(reopened.Level, reopened.SourceGeometry, out string? identityError);
            if (identityError is not null) throw new InvalidOperationException(identityError);
            var rows = ReadRows(output);
            if (rows.Count != ordered.Count) throw new FormatException("Serialized surface rows do not match the parsed surface identities.");
            var addresses = new Dictionary<Surface, (int Sector, int Surface)>();
            for (int sector = 0; sector < reopened.Level.Sectors.Count; sector++)
                for (int surface = 0; surface < reopened.Level.Sectors[sector].Surfaces.Count; surface++)
                    addresses.Add(reopened.Level.Sectors[sector].Surfaces[surface], (sector, surface));
            var items = new List<SurfaceVertexColorReadiness>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
            {
                var (count, components, samples, ambiguousConversion) = rows[i];
                var surface = ordered[i];
                if (count != surface.Corners.Count) throw new FormatException("Serialized corner count does not match the parsed surface.");
                var result = DynamicVertexColorReadiness.Evaluate(samples, components);
                if (components == 4 && ambiguousConversion)
                    result = new(VertexRgbStorage.Unknown, VertexRgbStorage.Retained, null,
                        "Serialized W/R/G decimal conversion differs between direct float32 and double-to-float32 rounding. This retail parser edge is unverified; current OpenMotS still retains the 4N row.");
                var address = addresses[surface];
                items.Add(new(i, address.Sector, address.Surface,
                    count, components == 1 ? "N" : "4N", result.Retail.ToString(), result.OpenMots.ToString(),
                    result.DistinguishingCorner, surface.Geo, surface.Light, surface.Adjoin is not null, result.Reason));
            }
            return new("assessed", hash, true, items.AsReadOnly(), Scope);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or NotSupportedException or OverflowException)
        { return new("unavailable", hash, null, [], "Serialized output could not be assessed: " + e.Message); }
    }

    private static List<(int Count, int Components, VertexColorSample[] Samples, bool AmbiguousConversion)> ReadRows(string text)
    {
        var reader = new JklReader(new StringReader(text));
        bool geometry = false;
        while (reader.Next())
        {
            if (reader.Current.StartsWith("SECTION:", StringComparison.Ordinal)) geometry = reader.Section == "GEORESOURCE";
            if (!geometry || JklReader.WordAt(reader.Current, 0) != "WORLD" || JklReader.WordAt(reader.Current, 1) != "SURFACES") continue;
            int total = JklReader.ParseInt(JklReader.WordAt(reader.Current, 2));
            if (total < 0) throw new FormatException("Negative surface count.");
            var result = new List<(int, int, VertexColorSample[], bool)>();
            for (int index = 0; index < total; index++)
            {
                if (!reader.Next()) throw new FormatException("Missing surface row.");
                string row = reader.Current;
                if (JklReader.ParseInt(JklReader.FirstWord(row).TrimEnd(':')) != index)
                    throw new FormatException("Surface row identity is not the expected global index.");
                var tokens = JklReader.Tokens(JklReader.StripIndex(row).Replace(',', ' '));
                if (tokens.Length < 9) throw new FormatException("Truncated surface row.");
                int count = JklReader.ParseInt(tokens[8]);
                if (count is < 3 or > 24) throw new FormatException("Surface corner count is outside the inspected engine range 3..24.");
                int first = 9 + 2 * count, remaining = tokens.Length - first;
                int components = remaining == count ? 1 : remaining == 4 * count ? 4 : 0;
                if (components == 0) throw new FormatException("Surface lighting row is neither N nor 4N.");
                bool ambiguousConversion = false;
                float Value(int at, bool inspectedChannel = true)
                {
                    // The inferred comparison uses float32. Retained arbitrary decimals
                    // can disagree across direct conversion versus the legacy atof path;
                    // do not claim retail rounding parity for those rare boundaries.
                    float parsed = float.Parse(tokens[at], NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (!float.IsFinite(parsed))
                        throw new FormatException("Lighting token is outside finite float32 range.");
                    if (inspectedChannel && parsed != (float)JklReader.ParseFloat(tokens[at])) ambiguousConversion = true;
                    return parsed;
                }
                var samples = new VertexColorSample[count];
                for (int c = 0; c < count; c++)
                {
                    int at = first + c * components;
                    float white = Value(at);
                    samples[c] = components == 1 ? new(white, white, white, white)
                        : new(white, Value(at + 1), Value(at + 2), Value(at + 3, inspectedChannel: false));
                }
                result.Add((count, components, samples, ambiguousConversion));
            }
            return result;
        }
        throw new FormatException("No GEORESOURCE surface table found.");
    }
}

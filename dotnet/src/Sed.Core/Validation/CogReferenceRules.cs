using System.Globalization;
using Sed.Core.Model;

namespace Sed.Core.Validation;

public enum CogIndexStatus { Invalid, Null, Level, UnplacedUnchecked, StaticUnchecked }
public enum CogReferenceKind { Other, Thing, Sector, Surface, Cog }
public sealed record CogParameterDefinition(string Name, CogReferenceKind Kind);

public sealed class ConsistencyOptions
{
    /// <summary>Only explicit retail selection enables the inferred retail capacity warning.</summary>
    public bool RetailProfile { get; init; }
    /// <summary>
    /// Opt-in point containment diagnostics. Each thing scans level geometry;
    /// unsupported geometry is reported as unverified, never guessed or reassigned.
    /// </summary>
    public bool CheckThingContainment { get; init; }
    /// <summary>Null means script schemas are unavailable; raw values are never guessed to be references.</summary>
    public Func<Cog, IReadOnlyList<CogParameterDefinition>?>? ResolveCogParameters { get; init; }
}

public static class CogReferenceRules
{
    public static bool TryIndex(string text, out int value)
    {
        value = 0;
        string token = text.Trim();
        if (token.Length == 0) return false;
        int firstDigit = token[0] is '+' or '-' ? 1 : 0;
        if (firstDigit == token.Length || token[firstDigit..].Any(c => c is < '0' or > '9')) return false;
        return int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    public static CogIndexStatus CheckCogIndex(string text, int placedCogCount)
    {
        if (!TryIndex(text, out int value)) return CogIndexStatus.Invalid;
        if (value < 0) return CogIndexStatus.Null;
        // sithCog_GetCogByIndex uses another world's COG array when bit 0x8000 is set.
        if ((value & 0x8000) != 0) return CogIndexStatus.StaticUnchecked;
        if (value < placedCogCount) return CogIndexStatus.Level;
        // Template COGs add runtime instances after placed COGs; this list cannot prove an upper bound.
        return CogIndexStatus.UnplacedUnchecked;
    }
}

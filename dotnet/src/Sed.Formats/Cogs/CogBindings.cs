using System.Globalization;
using Sed.Core.Editing;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Jkl;

namespace Sed.Formats.Cogs;

public enum CogBindingTargetStatus { Unknown, NotReference, Missing, Invalid, Null, Level, UnplacedUnchecked, StaticUnchecked }

public sealed record CogBindingTarget(CogReferenceKind Kind, CogBindingTargetStatus Status,
    int? Index = null, int? SectorIndex = null, int? SurfaceIndex = null, string? Detail = null);

public sealed record CogBindingRow(int CogIndex, int ParameterIndex, string? SymbolName,
    CogSymbolType Type, string? RawValue, CogSymbol? Symbol, CogBindingTarget Target,
    bool? EventLinkCandidate);

public sealed record CogBindingDescription(int CogIndex, string ScriptName, string ScriptStatus,
    bool SchemaComplete, int ActualParameterCount, int? ExpectedParameterCount,
    bool? ParameterCountMatches, IReadOnlyList<CogSchemaDiagnostic> Diagnostics,
    IReadOnlyList<CogBindingRow> Rows)
{
    public string EventLinkSemantics => "OpenJKDF2 reference-symbol modifiers; static candidate only, not runtime or multiplayer proof";
}

public sealed class CogBindingException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Read-only typed reference inspection and strictly bounded positional rebinding.
/// Scripts are neither executed nor rewritten. Resource identity and stale-plan
/// checks belong to the caller. Imported documents must use the document overload
/// so retained global surface slots are not confused with sector traversal order.
/// </summary>
public static class CogBindings
{
    /// <summary>
    /// Enumerates supported serialized global surface indexes and their current
    /// sector/local addresses. Refuses ambiguous retained geometry identity.
    /// </summary>
    public static IReadOnlyList<CogBindingTarget> SurfaceTargets(JklDocument document)
    {
        var map = SurfaceMap(document);
        if (map.Error is not null)
            throw new CogBindingException("unresolvedSurfaceIdentity", map.Error);
        var targets = new List<CogBindingTarget>(map.Surfaces.Count);
        for (int i = 0; i < map.Surfaces.Count; i++)
        {
            var surface = map.Surfaces[i];
            targets.Add(new(CogReferenceKind.Surface, CogBindingTargetStatus.Level, i,
                document.Level.Sectors.IndexOf(surface.Sector), surface.Sector.Surfaces.IndexOf(surface)));
        }
        return targets.AsReadOnly();
    }

    /// <summary>For a newly authored model whose serialized surfaces use sector order.</summary>
    public static CogBindingDescription Describe(Level level, int cogIndex, CogScript? script) =>
        Describe(new JklDocument(level), cogIndex, script);

    public static CogBindingDescription Describe(JklDocument document, int cogIndex, CogScript? script)
    {
        var level = document.Level;
        var cog = GetCog(level, cogIndex);
        string status = script is null ? "missing" :
            !SameScript(cog.Name, script.Name) ? "nameMismatch" : script.IsComplete ? "complete" : "incomplete";
        bool complete = status == "complete";
        int? expected = complete ? script!.LevelValues.Count : null;
        bool? countMatches = expected is int count ? count == cog.Values.Count : null;
        var diagnostics = script?.Diagnostics.ToList() ?? new List<CogSchemaDiagnostic>();
        if (status == "missing") diagnostics.Add(new("missingScript", null, "Script schema is unavailable; raw values are not guessed to be references."));
        if (status == "nameMismatch") diagnostics.Add(new("scriptNameMismatch", null, "The supplied script does not match this placed COG's script name."));
        if (countMatches == false)
            diagnostics.Add(new("parameterCountMismatch", null, $"The COG has {cog.Values.Count} values; its complete schema requires {expected}."));

        var map = SurfaceMap(document);
        var rows = new List<CogBindingRow>();
        int rowCount = System.Math.Max(cog.Values.Count, expected ?? 0);
        for (int i = 0; i < rowCount; i++)
        {
            var symbol = complete && i < script!.LevelValues.Count ? script.LevelValues[i] : null;
            string? rawValue = i < cog.Values.Count ? cog.Values[i] : null;
            var target = Resolve(level, symbol, rawValue, map.Surfaces, map.Error);
            bool? candidate = symbol is null ? null :
                target.Status == CogBindingTargetStatus.Level &&
                target.Kind is CogReferenceKind.Thing or CogReferenceKind.Sector or CogReferenceKind.Surface &&
                symbol.EffectiveLinkId >= 0 && symbol.EffectiveMask != 0;
            rows.Add(new(cogIndex, i, symbol?.Name, symbol?.Type ?? CogSymbolType.Unknown,
                rawValue, symbol, target, candidate));
        }
        return new(cogIndex, cog.Name, status, complete, cog.Values.Count, expected, countMatches,
            diagnostics.AsReadOnly(), rows.AsReadOnly());
    }

    /// <summary>For newly authored model-order geometry. Imported levels must use the document overload.</summary>
    public static SetCogValueCommand PrepareRebind(Level level, int cogIndex, int parameterIndex,
        string expectedSymbol, CogSymbolType expectedType, int targetIndex, CogScript? script) =>
        PrepareRebind(new JklDocument(level), cogIndex, parameterIndex, expectedSymbol, expectedType, targetIndex, script);

    /// <summary>
    /// Returns one shared undoable command without applying it. Requires exact
    /// existing layout, an expected symbol identity and a supported current-world
    /// target (or the explicit -1 null sentinel). Never pads the value list.
    /// </summary>
    public static SetCogValueCommand PrepareRebind(JklDocument document, int cogIndex, int parameterIndex,
        string expectedSymbol, CogSymbolType expectedType, int targetIndex, CogScript? script)
    {
        var description = Describe(document, cogIndex, script);
        if (!description.SchemaComplete)
            throw new CogBindingException("incompleteCogSchema", "A matching, completely understood script symbol schema is required for typed rebinding.");
        if (description.ParameterCountMatches != true)
            throw new CogBindingException("cogParameterCountMismatch", "Typed rebinding requires the exact existing parameter count; values will not be padded or discarded.");
        if ((uint)parameterIndex >= (uint)description.Rows.Count)
            throw new CogBindingException("cogParameterOutOfRange", "Parameter index is outside this COG's existing values.");
        var row = description.Rows[parameterIndex];
        if (string.IsNullOrWhiteSpace(expectedSymbol) ||
            !string.Equals(row.SymbolName, expectedSymbol, StringComparison.OrdinalIgnoreCase) || row.Type != expectedType)
            throw new CogBindingException("cogSymbolMismatch", "The expected symbol name and type must match the current positional schema.");
        if (row.Target.Kind == CogReferenceKind.Other)
            throw new CogBindingException("unsupportedCogBindingType", "Only thing, sector, surface and placed-COG references support this operation.");
        if (row.Target.Status is CogBindingTargetStatus.StaticUnchecked or CogBindingTargetStatus.UnplacedUnchecked)
            throw new CogBindingException("unsupportedCogReference", "An existing static-world or runtime/unplaced COG reference cannot be rebound by this placed-reference operation.");
        if (targetIndex < -1)
            throw new CogBindingException("invalidCogTarget", "Only -1 is accepted as an authored null target.");

        var level = document.Level;
        var map = SurfaceMap(document);
        // Even nulling a surface binding requires understood source identity: it
        // must not silently turn a wrongly interpreted positional link into a fix.
        if (row.Target.Kind == CogReferenceKind.Surface && map.Error is not null)
            throw new CogBindingException("unresolvedSurfaceIdentity", map.Error);
        var target = Resolve(level, row.Symbol, targetIndex.ToString(CultureInfo.InvariantCulture), map.Surfaces, map.Error);
        if (target.Status is not (CogBindingTargetStatus.Level or CogBindingTargetStatus.Null))
            throw new CogBindingException("invalidCogTarget", target.Detail ?? "Target is not a supported current-world reference.");
        return new SetCogValueCommand(level.Cogs[cogIndex], parameterIndex, targetIndex.ToString(CultureInfo.InvariantCulture));
    }

    private static Cog GetCog(Level level, int index)
    {
        if ((uint)index >= (uint)level.Cogs.Count)
            throw new CogBindingException("cogOutOfRange", "COG index is outside the placed COG list.");
        return level.Cogs[index];
    }

    private static bool SameScript(string left, string right) =>
        left.Replace('\\', '/').Equals(right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static CogReferenceKind Kind(CogSymbolType type) => type switch
    {
        CogSymbolType.Thing => CogReferenceKind.Thing,
        CogSymbolType.Sector => CogReferenceKind.Sector,
        CogSymbolType.Surface => CogReferenceKind.Surface,
        CogSymbolType.Cog => CogReferenceKind.Cog,
        _ => CogReferenceKind.Other,
    };

    private static CogBindingTarget Resolve(Level level, CogSymbol? symbol, string? value,
        IReadOnlyList<Surface> surfaces, string? surfaceError)
    {
        if (symbol is null) return new(CogReferenceKind.Other, CogBindingTargetStatus.Unknown,
            Detail: "No unambiguous symbol type is available for this raw parameter.");
        var kind = Kind(symbol.Type);
        if (value is null) return new(kind, CogBindingTargetStatus.Missing, Detail: "No authored positional value is present.");
        if (kind == CogReferenceKind.Other) return new(kind, CogBindingTargetStatus.NotReference);
        if (!CogReferenceRules.TryIndex(value, out int index))
            return new(kind, CogBindingTargetStatus.Invalid, Detail: "Reference is not a complete decimal 32-bit index.");
        if (index < 0)
            return new(kind, CogBindingTargetStatus.Null, index, Detail: index == -1 ? null : "Negative legacy sentinel retained; typed authoring only writes -1.");
        if (kind == CogReferenceKind.Cog)
        {
            var status = CogReferenceRules.CheckCogIndex(value, level.Cogs.Count);
            return status switch
            {
                CogIndexStatus.Level => new(kind, CogBindingTargetStatus.Level, index),
                CogIndexStatus.StaticUnchecked => new(kind, CogBindingTargetStatus.StaticUnchecked, index, Detail: "Static-world COG index; this placed level cannot resolve it."),
                CogIndexStatus.UnplacedUnchecked => new(kind, CogBindingTargetStatus.UnplacedUnchecked, index, Detail: "May identify a runtime/template instance beyond the placed COG list; unchecked."),
                _ => new(kind, CogBindingTargetStatus.Invalid, index),
            };
        }
        if (kind == CogReferenceKind.Surface)
        {
            if (surfaceError is not null) return new(kind, CogBindingTargetStatus.Unknown, index, Detail: surfaceError);
            if ((uint)index >= (uint)surfaces.Count)
                return new(kind, CogBindingTargetStatus.Invalid, index, Detail: "Surface index exceeds the current serialized surface count.");
            var surface = surfaces[index];
            int sectorIndex = level.Sectors.IndexOf(surface.Sector);
            int localIndex = surface.Sector.Surfaces.IndexOf(surface);
            return new(kind, CogBindingTargetStatus.Level, index, sectorIndex, localIndex);
        }
        int count = kind == CogReferenceKind.Thing ? level.Things.Count : level.Sectors.Count;
        return index < count ? new(kind, CogBindingTargetStatus.Level, index) :
            new(kind, CogBindingTargetStatus.Invalid, index, Detail: $"Reference exceeds the current placed {kind.ToString().ToLowerInvariant()} count {count}.");
    }

    private static (IReadOnlyList<Surface> Surfaces, string? Error) SurfaceMap(JklDocument document)
    {
        try
        {
            JklSourceGeometry.ValidateRetainedSource(document);
            var surfaces = GeoResourceWriter.OrderedSurfaces(document.Level, document.SourceGeometry, out string? error);
            if (surfaces.Distinct().Count() != surfaces.Count)
                error ??= "A surface has multiple current owners.";
            var globalIndices = new Dictionary<Surface, int>();
            for (int i = 0; i < surfaces.Count; i++) globalIndices.TryAdd(surfaces[i], i);
            foreach (var sector in document.Level.Sectors)
            {
                foreach (var surface in sector.Surfaces)
                    if (!ReferenceEquals(surface.Sector, sector)) error ??= "A surface's owning sector does not match its current list membership.";
                for (int i = 1; i < sector.Surfaces.Count; i++)
                    if (globalIndices[sector.Surfaces[i]] != globalIndices[sector.Surfaces[0]] + i)
                        error ??= "A sector no longer has a contiguous serialized surface range.";
            }
            return (surfaces, error);
        }
        catch (InvalidOperationException error)
        {
            return (Array.Empty<Surface>(), error.Message);
        }
    }
}

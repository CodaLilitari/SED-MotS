using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Lighting;

public enum LightDiagnosticStatus
{
    Contributing, NoEligibleSamples, ZeroContribution, NonfiniteContribution,
    DisabledRange, ZeroIntensity, InvalidLight, UnsupportedGeometry,
}

/// <summary>
/// Counts are surface corners, not distinct vertices. For an evaluated light,
/// Candidate = FacingRejected + RangeRejected + Occluded + Eligible. Shadow
/// counters cover in-range, facing samples only. Nonzero counts describe this
/// light's finite isolated contribution, not the final accumulated bake.
/// </summary>
public sealed record LightSampleCounts(long Candidate, long FacingRejected, long RangeRejected,
    long ShadowTested, long ShadowBypassedDisabled, long ShadowBypassedSameSector, long ShadowBypassedNoBlock,
    long Occluded, long Eligible, long NonzeroMono, long NonzeroColor, long NonfiniteContribution);

public sealed record LightBakeDiagnostic(int LightIndex, LightDiagnosticStatus Status,
    int? BakerSectorIndex, LightSampleCounts? Samples, string? Detail);

/// <summary>
/// Immutable world-bake explanation. Null sample counts mean unverified, not zero.
/// UpdateSectorAmbients records the requested bake option; diagnostics neither
/// apply nor predict the subsequent aggregate ambient update. ARCH is excluded.
/// </summary>
public sealed record WorldLightingDiagnostics(ProjectType Game, bool CastShadows, bool UpdateSectorAmbients,
    IReadOnlyList<int> TargetSectorIndices, int? TargetSurfaces, long? CandidateSamples,
    string? GeometryIssue, IReadOnlyList<LightBakeDiagnostic> Lights);

public static partial class LightCalculator
{
    /// <summary>Conservative per-query target-corner × selected-light budget, before any sample decisions.</summary>
    public const long MaxDiagnosticSampleLightPairs = 50_000_000;

    /// <summary>
    /// Builds the world's shadow grid without mutating cached normals. The
    /// returned predicate is true when the segment is blocked, using the same
    /// adjoin/BlockLight and endpoint tolerances as the world bake. It excludes
    /// no surface and applies no light flags or same-sector shortcut. The level
    /// geometry and surface/adjoin data must remain unchanged during its use.
    /// Calls are sequential: the grid reuses private visit stamps.
    /// </summary>
    public static Func<Vec3, Vec3, bool> CreateWorldOcclusion(Level level)
    {
        ValidateWorldOcclusionGeometry(level);
        var grid = new SurfaceGrid(BuildPlaneCache(level, updateNormals: false));
        return (from, to) =>
        {
            if (!DiagnosticFinite(from)) throw new ArgumentException("Occlusion endpoints must be finite.", nameof(from));
            if (!DiagnosticFinite(to)) throw new ArgumentException("Occlusion endpoints must be finite.", nameof(to));
            if (!DiagnosticFinite(to - from)) throw new ArgumentException("Occlusion segment span must be finite.", nameof(to));
            return grid.IsBlocked(from, to, exclude: null);
        };
    }

    /// <summary>
    /// Validates reference ownership and finite arithmetic needed by the shadow
    /// grid without building it or changing caches. This is not closed-volume,
    /// polygon-planarity or reciprocal-adjoin validation.
    /// </summary>
    public static void ValidateWorldOcclusionGeometry(Level level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (DiagnosticGeometryIssue(level) is { } issue)
            throw new InvalidOperationException($"World occlusion is unavailable: {issue}");
    }

    /// <summary>
    /// Explains selected world lights using the bake's activation, facing, range,
    /// parity-sector shortcut, shadow grid and falloff calculations without
    /// changing normals, intensities or ambients. Targets must be live sectors;
    /// duplicate targets/light indices are evaluated once. A batch builds one
    /// grid and scans target corners per selected light. It is not a cheap asset
    /// inventory query, a full geometry validator, or a runtime/retail prediction.
    /// </summary>
    public static WorldLightingDiagnostics Diagnose(Level level, IReadOnlyList<Sector>? targets = null,
        LightingOptions? options = null, IReadOnlyList<int>? lightIndices = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        options ??= new LightingOptions();
        var selected = (targets ?? level.Sectors).Distinct().ToArray();
        var targetIndices = selected.Select(sector => level.Sectors.IndexOf(sector)).ToArray();
        if (targetIndices.Any(index => index < 0))
            throw new ArgumentException("Lighting diagnostic targets must be live level sectors.", nameof(targets));
        var indices = (lightIndices ?? Enumerable.Range(0, level.Lights.Count).ToArray()).Distinct().ToArray();
        if (indices.Any(index => (uint)index >= (uint)level.Lights.Count))
            throw new ArgumentOutOfRangeException(nameof(lightIndices));

        string? geometryIssue = DiagnosticGeometryIssue(level);
        int? surfaceCount = geometryIssue is null ? selected.Sum(sector => sector.Surfaces.Count) : null;
        long? candidateCount = geometryIssue is null
            ? selected.Sum(sector => sector.Surfaces.Sum(surface => (long)surface.Corners.Count)) : null;
        // Division avoids overflow in a caller-controlled count product. Charge
        // skipped lights too: this is a conservative work bound, not a prediction.
        if (indices.Length > 0 && candidateCount > MaxDiagnosticSampleLightPairs / indices.Length)
            throw new InvalidOperationException($"Lighting diagnostics exceed the {MaxDiagnosticSampleLightPairs} sample-light pair budget; select fewer lights or target sectors.");
        var grid = geometryIssue is null ? new SurfaceGrid(BuildPlaneCache(level, updateNormals: false)) : null;
        var diagnostics = new List<LightBakeDiagnostic>();
        bool rgb = level.Kind != ProjectType.JediKnight;
        var normals = geometryIssue is null
            ? selected.SelectMany(sector => sector.Surfaces).Distinct().ToDictionary(surface => surface, surface => surface.CalculateNormal())
            : new Dictionary<Surface, Vec3>();

        foreach (int index in indices)
        {
            var light = level.Lights[index];
            string? invalid = DiagnosticLightIssue(level.Kind, light);
            if (invalid is not null)
            {
                diagnostics.Add(new(index, LightDiagnosticStatus.InvalidLight, null, null, invalid));
                continue;
            }
            if (geometryIssue is not null)
            {
                diagnostics.Add(new(index, LightDiagnosticStatus.UnsupportedGeometry, null, null, geometryIssue));
                continue;
            }

            var lightSector = FindSector(level, light.Position, updateNormals: false);
            int containingIndex = lightSector is null ? -1 : level.Sectors.IndexOf(lightSector);
            int? bakerSectorIndex = containingIndex < 0 ? null : containingIndex;
            double rgbIntensity = EffectiveRgbIntensity(level.Kind, light);
            long candidate = candidateCount!.Value;
            if (SkipLight(light, rgb, rgbIntensity))
            {
                diagnostics.Add(new(index, light.Range <= 0 ? LightDiagnosticStatus.DisabledRange : LightDiagnosticStatus.ZeroIntensity,
                    bakerSectorIndex, new(candidate, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
                    light.Range <= 0 ? "The bake skips nonpositive primary ranges."
                        : "Both consumed intensity channels are zero; no sample tests run."));
                continue;
            }

            long facing = 0, range = 0, tested = 0, disabled = 0, same = 0, noBlock = 0;
            long occluded = 0, eligible = 0, mono = 0, color = 0, nonfinite = 0;
            foreach (var sector in selected)
                foreach (var surface in sector.Surfaces)
                {
                    if (surface.Corners.Count == 0) continue;
                    if (BehindSurface(normals[surface], surface.Corners[0].Vertex.Position, light.Position))
                    { facing += surface.Corners.Count; continue; }
                    bool sameSector = ReferenceEquals(lightSector, sector);
                    bool checkShadows = options.CastShadows && !sameSector && (light.Flags & LightFlags.NoBlock) == 0;
                    foreach (var corner in surface.Corners)
                    {
                        var sample = EvaluateSample(light, rgbIntensity, corner.Vertex.Position, surface, checkShadows, grid!);
                        if (sample.Status == SampleStatus.OutOfRange) { range++; continue; }
                        if (checkShadows) tested++;
                        else if (!options.CastShadows) disabled++;
                        else if (sameSector) same++;
                        else noBlock++;
                        if (sample.Status == SampleStatus.Shadowed) { occluded++; continue; }
                        eligible++;
                        var outputColor = rgb ? light.Color * sample.Rgb : Grey(sample.Mono);
                        bool finiteMono = float.IsFinite(sample.Mono);
                        bool finiteColor = float.IsFinite(outputColor.R) && float.IsFinite(outputColor.G) && float.IsFinite(outputColor.B);
                        if (!finiteMono || !finiteColor) nonfinite++;
                        if (finiteMono && sample.Mono != 0) mono++;
                        if (finiteColor && (outputColor.R != 0 || outputColor.G != 0 || outputColor.B != 0)) color++;
                    }
                }
            var status = nonfinite > 0 ? LightDiagnosticStatus.NonfiniteContribution
                : eligible == 0 ? LightDiagnosticStatus.NoEligibleSamples
                : mono == 0 && color == 0 ? LightDiagnosticStatus.ZeroContribution : LightDiagnosticStatus.Contributing;
            string? detail = status switch
            {
                LightDiagnosticStatus.NonfiniteContribution => "At least one isolated contribution is nonfinite; these counts do not certify a usable bake.",
                LightDiagnosticStatus.NoEligibleSamples => "No target corners passed the facing, range and shadow predicates.",
                LightDiagnosticStatus.ZeroContribution => "Eligible corners produce zero in both output channels, including color multiplication and float rounding.",
                _ => null,
            };
            diagnostics.Add(new(index, status, bakerSectorIndex,
                new(candidate, facing, range, tested, disabled, same, noBlock, occluded, eligible, mono, color, nonfinite), detail));
        }

        return new(level.Kind, options.CastShadows, options.UpdateSectorAmbients,
            Array.AsReadOnly(targetIndices), surfaceCount, candidateCount, geometryIssue, diagnostics.AsReadOnly());
    }

    private static string? DiagnosticLightIssue(ProjectType kind, Light? light)
    {
        if (light is null) return "The light reference is null.";
        if (!DiagnosticFinite(light.Position) || !double.IsFinite(light.Range) || !double.IsFinite(light.Intensity))
            return "The light has a nonfinite position, primary range or mono intensity.";
        // Only consumed source fields are relevant here. General authoring
        // validation separately checks unused metadata such as LegacyRgbRange.
        if (kind != ProjectType.JediKnight && (!double.IsFinite(EffectiveRgbIntensity(kind, light))
            || !float.IsFinite(light.Color.R) || !float.IsFinite(light.Color.G) || !float.IsFinite(light.Color.B)))
            return "The light has a nonfinite consumed RGB intensity or color.";
        return null;
    }

    private static string? DiagnosticGeometryIssue(Level level)
    {
        var sectors = new HashSet<Sector>();
        var bounds = Box.Empty;
        bool hasVertices = false;
        for (int si = 0; si < level.Sectors.Count; si++)
        {
            var sector = level.Sectors[si];
            if (sector is null || sector.Level != level || !sectors.Add(sector))
                return $"Sector {si} is null, duplicated or has a different level owner.";
            var vertices = new HashSet<Vertex>();
            foreach (var vertex in sector.Vertices)
            {
                if (vertex is null || vertex.Sector != sector || !vertices.Add(vertex) || !DiagnosticFinite(vertex.Position))
                    return $"Sector {si} has an invalid vertex reference or nonfinite coordinates.";
                bounds.Encapsulate(vertex.Position);
                hasVertices = true;
            }
            var surfaces = new HashSet<Surface>();
            for (int fi = 0; fi < sector.Surfaces.Count; fi++)
            {
                var surface = sector.Surfaces[fi];
                if (surface is null || surface.Sector != sector || !surfaces.Add(surface)
                    || surface.Corners.Any(corner => corner is null || corner.Vertex is null || !vertices.Contains(corner.Vertex)))
                    return $"Sector {si}, surface {fi} has invalid ownership or corner references.";
                var normal = surface.CalculateNormal();
                if (!DiagnosticFinite(normal) || (surface.Corners.Count > 0 && !double.IsFinite(normal.Dot(surface.Corners[0].Vertex.Position))))
                    return $"Sector {si}, surface {fi} has unrepresentable plane arithmetic.";
            }
        }
        if (hasVertices && !DiagnosticFinite(bounds.Max - bounds.Min))
            return "The level bounds have an unrepresentable span for the shadow grid.";
        return null;
    }

    private static bool DiagnosticFinite(Vec3 point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y) && double.IsFinite(point.Z);
}

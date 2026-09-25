using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Query;

namespace Sed.Core.Validation;

public enum IssueSeverity { Error, Warning }
public enum ItemType { Sector, Surface, Thing, Cog, Light }
public sealed record ConsistencyIssue(ItemType Type, IssueSeverity Severity, int SectorIndex, int SurfaceIndex, string Message)
{
    public int ItemIndex { get; init; } = -1;
    public int ParameterIndex { get; init; } = -1;
}

public static class ConsistencyChecker
{
    /// <summary>Read-only validation. Invalid references are checked before geometry is accessed.</summary>
    public static List<ConsistencyIssue> Check(Level level, ConsistencyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        var issues = new List<ConsistencyIssue>();
        var sectors = level.Sectors.Where(s => s is not null).ToHashSet();
        var surfaces = sectors.SelectMany(s => s.Surfaces).Where(s => s is not null).ToHashSet();
        int maxCorners = level.Kind == ProjectType.InfernalMachine ? 64 : 24;
        var seenSectors = new HashSet<Sector>();
        var seenSurfaces = new HashSet<Surface>();
        for (int si = 0; si < level.Sectors.Count; si++)
        {
            var sector = level.Sectors[si];
            if (sector is null)
            {
                issues.Add(new(ItemType.Sector, IssueSeverity.Error, si, -1, "Null sector reference"));
                continue;
            }
            if (!seenSectors.Add(sector) || sector.Level != level)
                issues.Add(new(ItemType.Sector, IssueSeverity.Error, si, -1, "Duplicate sector or incorrect level owner"));
            if (sector.Surfaces.Count < 4)
                issues.Add(new(ItemType.Sector, IssueSeverity.Error, si, -1, "Sector has fewer than 4 surfaces"));
            if (sector.Vertices.Count < 4)
                issues.Add(new(ItemType.Sector, IssueSeverity.Error, si, -1, "Sector has fewer than 4 vertices"));
            if (sector.Vertices.Any(v => v is null || !Finite(v.Position)))
                issues.Add(new(ItemType.Sector, IssueSeverity.Error, si, -1, "Sector has a null vertex or non-finite position"));
            if (!Finite(sector.Ambient) || !Finite(sector.ExtraLight) || !Finite(sector.Tint) || !Finite(sector.Thrust)
                || !double.IsFinite(sector.SoundVolume))
                issues.Add(new(ItemType.Sector, IssueSeverity.Error, si, -1, "Sector has non-finite lighting, thrust or sound volume"));
            var vertices = sector.Vertices.ToHashSet();
            for (int sfi = 0; sfi < sector.Surfaces.Count; sfi++)
            {
                var surf = sector.Surfaces[sfi];
                if (surf is null)
                {
                    issues.Add(new(ItemType.Surface, IssueSeverity.Error, si, sfi, "Null surface reference"));
                    continue;
                }
                if (!seenSurfaces.Add(surf) || surf.Sector != sector)
                    issues.Add(new(ItemType.Surface, IssueSeverity.Error, si, sfi, "Duplicate surface or incorrect sector owner"));
                CheckSurface(issues, surf, vertices, surfaces, si, sfi, maxCorners, level.Kind);
            }
        }
        for (int ti = 0; ti < level.Things.Count; ti++)
        {
            var thing = level.Things[ti];
            if (thing is null)
            {
                issues.Add(new(ItemType.Thing, IssueSeverity.Error, -1, -1, "Null thing reference") { ItemIndex = ti });
                continue;
            }
            if (thing.Sector is null)
                issues.Add(new(ItemType.Thing, IssueSeverity.Warning, -1, -1, "Thing is not in a sector") { ItemIndex = ti });
            else if (!sectors.Contains(thing.Sector))
                issues.Add(new(ItemType.Thing, IssueSeverity.Error, -1, -1, "Thing references a sector outside this level") { ItemIndex = ti });
            if (!Finite(thing.Position) || !double.IsFinite(thing.Pitch) || !double.IsFinite(thing.Yaw) || !double.IsFinite(thing.Roll))
                issues.Add(new(ItemType.Thing, IssueSeverity.Error, -1, -1, "Thing has a non-finite position or rotation") { ItemIndex = ti });
            if (options?.CheckThingContainment == true)
                CheckThingContainment(level, ti, issues);
        }
        for (int li = 0; li < level.Lights.Count; li++)
        {
            var light = level.Lights[li];
            if (light is null || !Finite(light.Position) || !Finite(light.Color) || !double.IsFinite(light.Intensity) || !double.IsFinite(light.Range)
                || light.RgbIntensity is { } rgbIntensity && !double.IsFinite(rgbIntensity)
                || light.LegacyRgbRange is { } rgbRange && !double.IsFinite(rgbRange))
                issues.Add(new(ItemType.Light, IssueSeverity.Error, -1, -1, "Light has null or non-finite data") { ItemIndex = li });
        }
        int serializedThingCapacity = System.Math.Max(level.ThingCapacity, level.Things.Count);
        if (options?.RetailProfile == true && level.Kind != ProjectType.InfernalMachine && serializedThingCapacity > 640)
            issues.Add(new(ItemType.Thing, IssueSeverity.Warning, -1, -1,
                $"Retail compatibility: {level.Things.Count} placed things, serialized capacity {serializedThingCapacity} (declared {level.ThingCapacity}). " +
                "Serialized capacity exceeds the inferred non-QOL pointer range of 640; runtime spawning also needs headroom. This is not a complete retail capacity model."));
        CheckCogs(level, options, issues);
        return issues;
    }

    private static void CheckThingContainment(Level level, int thingIndex, List<ConsistencyIssue> issues)
    {
        var result = ThingContainment.Evaluate(level, thingIndex);
        if (result.Status == ThingContainmentStatus.UnsupportedGeometry)
            issues.Add(new(ItemType.Thing, IssueSeverity.Warning, result.AssignedSectorIndex ?? -1, -1,
                $"Thing containment was not verified: {result.Detail}") { ItemIndex = thingIndex });
        else if (result.Assignment == ThingSectorAssignmentStatus.NotContained)
        {
            string candidates = result.Candidates.Count == 0 ? "none"
                : string.Join(", ", result.Candidates.Select(candidate => candidate.SectorIndex));
            issues.Add(new(ItemType.Thing, IssueSeverity.Warning, result.AssignedSectorIndex ?? -1, -1,
                $"Thing position is not contained by its assigned sector {result.AssignedSectorIndex}; containing sector candidates: {candidates}. " +
                "No sector assignment was changed.") { ItemIndex = thingIndex });
        }
        // Existing reference checks cover unassigned/outside-level things.
        // Multiple candidates are legitimate when the assigned sector is among them.
    }

    private static void CheckSurface(List<ConsistencyIssue> issues, Surface surf, HashSet<Vertex> vertices,
        HashSet<Surface> surfaces, int si, int sfi, int maxCorners, ProjectType game)
    {
        void Error(string message) => issues.Add(new(ItemType.Surface, IssueSeverity.Error, si, sfi, message));
        if (surf.Adjoin is not null)
        {
            if (!surfaces.Contains(surf.Adjoin)) Error("Adjoin references a surface outside this level");
            else if (surf.Adjoin == surf || surf.Adjoin.Adjoin != surf) Error("Invalid reverse adjoin");
        }
        if (surf.Corners.Count > maxCorners)
            issues.Add(new(ItemType.Surface, game == ProjectType.InfernalMachine ? IssueSeverity.Warning : IssueSeverity.Error,
                si, sfi, game == ProjectType.InfernalMachine ? "Surface exceeds max vertex count (64)"
                    : "Surface exceeds max vertex count (24); JK/MotS engine parsing rejects this surface"));
        if (surf.Corners.Count < 3) { Error("Surface has fewer than 3 vertices"); return; }
        if (surf.Corners.Any(c => c is null || c.Vertex is null || !vertices.Contains(c.Vertex)))
        { Error("Surface references a missing vertex outside its owning sector"); return; }
        if (surf.Corners.Any(c => !Finite(c.Vertex.Position)))
        { Error("Surface has a non-finite vertex position"); return; }
        // Duplicate adjacent positions survive Newell's area calculation when
        // the rest of the polygon still has area, but leave a zero-length edge.
        const double minimumEdgeLengthSquared = 1e-18;
        for (int i = 0; i < surf.Corners.Count; i++)
            if (surf.Corners[i].Vertex.Position.DistanceSquaredTo(surf.Corners[(i + 1) % surf.Corners.Count].Vertex.Position)
                <= minimumEdgeLengthSquared)
            { Error("Surface contains a zero-length edge (adjacent vertices within 1e-9 units)"); break; }
        if (!float.IsFinite(surf.ExtraLightIntensity) || !float.IsFinite(surf.UScale) || !float.IsFinite(surf.VScale)
            || surf.Corners.Any(c => !double.IsFinite(c.Uv.U) || !double.IsFinite(c.Uv.V) || !Finite(c.Intensity)
                || (c.MonoIntensity is float mono && !float.IsFinite(mono))))
            Error("Surface has non-finite texture coordinates or lighting");

        // Newell's method, computed locally: checking does not change the cached renderer normal.
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < surf.Corners.Count; i++)
        {
            var cur = surf.Corners[i].Vertex.Position;
            var next = surf.Corners[(i + 1) % surf.Corners.Count].Vertex.Position;
            nx += (cur.Y - next.Y) * (cur.Z + next.Z);
            ny += (cur.Z - next.Z) * (cur.X + next.X);
            nz += (cur.X - next.X) * (cur.Y + next.Y);
        }
        var normal = new Vec3(nx, ny, nz).Normalized();
        if (!Finite(normal) || System.Math.Abs(normal.LengthSquared - 1.0) > 0.01)
        {
            issues.Add(new(ItemType.Surface, game == ProjectType.InfernalMachine ? IssueSeverity.Warning : IssueSeverity.Error,
                si, sfi, "Surface normal is invalid; the polygon has zero area or unrepresentable geometry"));
            return;
        }
        var p0 = surf.Corners[0].Vertex.Position;
        if (surf.Corners.Any(c => System.Math.Abs(normal.Dot(c.Vertex.Position - p0)) > 0.001))
            issues.Add(new(ItemType.Surface, IssueSeverity.Warning, si, sfi, "Surface is not planar"));
    }

    private static void CheckCogs(Level level, ConsistencyOptions? options, List<ConsistencyIssue> issues)
    {
        if (options?.ResolveCogParameters is not { } resolve) return;
        int surfaceCount = level.Sectors.Where(s => s is not null).Sum(s => s.Surfaces.Count);
        for (int ci = 0; ci < level.Cogs.Count; ci++)
        {
            var cog = level.Cogs[ci];
            var parameters = cog is null ? null : resolve(cog);
            if (parameters is null)
            {
                issues.Add(new(ItemType.Cog, IssueSeverity.Warning, -1, -1,
                    "COG script schema unavailable; positional references were not checked") { ItemIndex = ci });
                continue;
            }
            for (int pi = 0; pi < System.Math.Min(parameters.Count, cog!.Values.Count); pi++)
            {
                var parameter = parameters[pi];
                if (parameter.Kind == CogReferenceKind.Other) continue;
                string valueText = cog.Values[pi];
                string? problem = null;
                IssueSeverity severity = IssueSeverity.Error;
                if (!CogReferenceRules.TryIndex(valueText, out int value)) problem = "must be a complete decimal 32-bit index";
                else if (parameter.Kind == CogReferenceKind.Cog)
                {
                    var status = CogReferenceRules.CheckCogIndex(valueText, level.Cogs.Count);
                    if (status is CogIndexStatus.StaticUnchecked or CogIndexStatus.UnplacedUnchecked)
                    {
                        severity = IssueSeverity.Warning;
                        problem = status == CogIndexStatus.StaticUnchecked
                            ? "references a static-world COG; its runtime bounds are unverified"
                            : "references an unplaced/runtime COG; the placed list cannot establish its bounds";
                    }
                }
                else
                {
                    int count = parameter.Kind switch
                    {
                        CogReferenceKind.Thing => level.Things.Count,
                        CogReferenceKind.Sector => level.Sectors.Count,
                        CogReferenceKind.Surface => surfaceCount,
                        _ => 0,
                    };
                    if (value >= count) problem = $"index {value} is outside the level's {parameter.Kind.ToString().ToLowerInvariant()} range (count {count})";
                }
                if (problem is not null)
                    issues.Add(new(ItemType.Cog, severity, -1, -1, $"COG {ci} parameter {parameter.Name}: {problem}")
                        { ItemIndex = ci, ParameterIndex = pi });
            }
        }
    }

    private static bool Finite(Vec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
    private static bool Finite(ColorF c) => float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B);
}

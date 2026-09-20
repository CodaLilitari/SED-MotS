using Sed.Core.Math;
using Sed.Core.Model;
using System.Security.Cryptography;
using System.Text;

namespace Sed.Formats.Jkl;

/// <summary>
/// Parsed table identities, scoped to this document's object instances. These
/// are not persistent editor IDs or permission to rewrite opaque references.
/// </summary>
internal sealed class JklSourceGeometry
{
    public required Sector[] Sectors { get; init; }
    public required int?[] SectorNumbers { get; init; }
    public required Surface?[] Surfaces { get; init; }
    public required Dictionary<Surface, Sector> SurfaceOwners { get; init; }
    public required int ParsedSurfaceCount { get; init; }
    public required Vec3[] Vertices { get; init; }
    public required Dictionary<Vertex, int> VertexSlots { get; init; }
    public required (long Flags, int Mirror)[] Adjoins { get; init; }
    public required Dictionary<Surface, int> AdjoinSlots { get; init; }
    public string? AdjoinProblem { get; init; }
    public string? ColormapProblem { get; init; }
    public required (Surface Surface, int SourceSurface, int Corner, int Index)[] InvalidTextureVertices { get; init; }
    public required string SourceTextHash { get; init; }

    public static JklSourceGeometry Capture(Level level, GeoData geo, string[] sourceLines) => new()
    {
        Sectors = level.Sectors.ToArray(),
        SectorNumbers = geo.SectorNumbers.ToArray(),
        Surfaces = Enumerable.Range(0, geo.Surfaces.Count)
            .Select(i => geo.SurfaceByGlobalIndex.GetValueOrDefault(i)).ToArray(),
        SurfaceOwners = geo.SurfaceByGlobalIndex.Values.Distinct().ToDictionary(s => s, s => s.Sector),
        ParsedSurfaceCount = level.Sectors.Sum(s => s.Surfaces.Count),
        Vertices = geo.Vertices.ToArray(),
        VertexSlots = level.Sectors.SelectMany(s => s.Vertices).Distinct()
            .Where(v => v.SourceIndex >= 0).ToDictionary(v => v, v => v.SourceIndex),
        Adjoins = geo.Adjoins.Select(a => (a.Flags, a.Mirror)).ToArray(),
        AdjoinSlots = geo.SurfaceByGlobalIndex.Where(pair => geo.Surfaces[pair.Key].Adjoin >= 0)
            .ToDictionary(pair => pair.Value, pair => geo.Surfaces[pair.Key].Adjoin),
        AdjoinProblem = FindAdjoinProblem(geo),
        ColormapProblem = geo.ColormapProblems.FirstOrDefault(),
        InvalidTextureVertices = geo.SurfaceByGlobalIndex.SelectMany(pair => geo.Surfaces[pair.Key].Tvxs
            .Select((index, corner) => (Surface: pair.Value, SourceSurface: pair.Key, Corner: corner, Index: index))
            .Where(value => (uint)value.Index >= (uint)geo.TexVertices.Count)).ToArray(),
        SourceTextHash = ComputeSourceTextHash(sourceLines),
    };

    internal static void ValidateRetainedSource(JklDocument document)
    {
        if (document.SourceGeometry is { } source &&
            source.SourceTextHash != ComputeSourceTextHash(document.SourceLines))
            throw new InvalidOperationException("Source geometry text changed outside the document model. Reopen the changed source before regenerating geometry; captured index identities no longer describe it.");
    }

    private static string ComputeSourceTextHash(string[] sourceLines)
    {
        var selected = new StringBuilder();
        bool include = false;
        foreach (string raw in sourceLines)
        {
            var fields = JklReader.Tokens(raw.Split('#')[0]);
            if (fields.Length > 0 && fields[0].Equals("SECTION:", StringComparison.OrdinalIgnoreCase))
                include = fields.Length >= 2 && (fields[1].Equals("GEORESOURCE", StringComparison.OrdinalIgnoreCase) ||
                    fields[1].Equals("SECTORS", StringComparison.OrdinalIgnoreCase));
            if (include) selected.Append(raw).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selected.ToString())));
    }

    private static string? FindAdjoinProblem(GeoData geo)
    {
        var owners = new HashSet<int>();
        foreach (var surface in geo.Surfaces)
        {
            int slot = surface.Adjoin;
            if (slot == -1) continue;
            if ((uint)slot >= (uint)geo.Adjoins.Count)
                return $"Imported surface references missing adjoin {slot}.";
            if (!owners.Add(slot)) return $"Imported adjoin {slot} has multiple surface owners.";
            int mirror = geo.Adjoins[slot].Mirror;
            if ((uint)mirror >= (uint)geo.Adjoins.Count || mirror == slot || geo.Adjoins[mirror].Mirror != slot)
                return $"Imported adjoin {slot} has an unsupported or nonreciprocal mirror.";
            int partner = geo.Adjoins[mirror].Surf;
            if ((uint)partner >= (uint)geo.Surfaces.Count || geo.Surfaces[partner].Adjoin != mirror)
                return $"Imported adjoin {slot} has no surface owning its mirror.";
        }
        return null;
    }
}

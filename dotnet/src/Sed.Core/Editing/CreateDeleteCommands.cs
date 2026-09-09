using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>Adds a thing to the level (reversible).</summary>
public sealed class CreateThingCommand : IEditCommand
{
    private readonly Level _level;
    public Thing Thing { get; }

    public CreateThingCommand(Level level, Thing thing) { _level = level; Thing = thing; }

    public string Name => "Create thing";
    public void Apply() { Thing.Level = _level; _level.Things.Add(Thing); _level.RenumberThings(); }
    public void Revert() { _level.Things.Remove(Thing); _level.RenumberThings(); }
}

/// <summary>Removes a thing from the level, remembering its position for undo.</summary>
public sealed class DeleteThingCommand : IEditCommand
{
    private readonly Level _level;
    private readonly Thing _thing;
    private int _index;

    public DeleteThingCommand(Level level, Thing thing) { _level = level; _thing = thing; }

    public string Name => "Delete thing";

    public void Apply()
    {
        _index = _level.Things.IndexOf(_thing);
        if (_index >= 0) _level.Things.RemoveAt(_index);
        _level.RenumberThings();
    }

    public void Revert()
    {
        if (_index >= 0) _level.Things.Insert(System.Math.Min(_index, _level.Things.Count), _thing);
        _level.RenumberThings();
    }
}

/// <summary>Adds a sector to the level (reversible).</summary>
public sealed class CreateSectorCommand : IEditCommand
{
    private readonly Level _level;
    public Sector Sector { get; }

    public CreateSectorCommand(Level level, Sector sector) { _level = level; Sector = sector; }

    public string Name => "Create sector";
    public void Apply() { _level.Sectors.Add(Sector); _level.RenumberSectors(); }
    public void Revert() { _level.Sectors.Remove(Sector); _level.RenumberSectors(); }
}

/// <summary>Removes a sector from the level, remembering its position for undo.</summary>
/// <summary>
/// Removes a sector from the level, and clears any adjoin that pointed *into* it
/// (mirroring the original's <c>RemoveSecRefs</c>). Leaving those behind would
/// give surviving sectors portals opening onto a sector the level no longer has —
/// the consistency checker flags it and the engine would render through a hole.
/// </summary>
public sealed class DeleteSectorCommand : IEditCommand
{
    private readonly Level _level;
    private readonly Sector _sector;
    private int _index;

    // Adjoins in other sectors that referenced this one, so undo can restore them.
    private readonly List<(Surface surface, Surface partner, long flags)> _inbound = new();

    public DeleteSectorCommand(Level level, Sector sector) { _level = level; _sector = sector; }

    public string Name => "Delete sector";

    public void Apply()
    {
        _index = _level.Sectors.IndexOf(_sector);
        if (_index >= 0) _level.Sectors.RemoveAt(_index);

        _inbound.Clear();
        foreach (var other in _level.Sectors)
            foreach (var surf in other.Surfaces)
            {
                if (surf.Adjoin is not { } partner) continue;
                if (!ReferenceEquals(partner.Sector, _sector)) continue;

                _inbound.Add((surf, partner, surf.AdjoinFlags));
                surf.Adjoin = null;
                surf.AdjoinFlags = 0;
            }

        _level.RenumberSectors();
    }

    public void Revert()
    {
        if (_index >= 0) _level.Sectors.Insert(System.Math.Min(_index, _level.Sectors.Count), _sector);

        foreach (var (surface, partner, flags) in _inbound)
        {
            surface.Adjoin = partner;
            surface.AdjoinFlags = flags;
        }
        _inbound.Clear();

        _level.RenumberSectors();
    }
}

/// <summary>
/// Deletes a sector vertex, removing its corners from every surface and dropping
/// any surface that falls below three corners. Fully reversible.
/// </summary>
public sealed class DeleteVertexCommand : IEditCommand
{
    private readonly Sector _sector;
    private readonly Vertex _vertex;
    private int _vertexIndex;
    private readonly List<(Surface surf, int idx, Surface.Corner corner)> _removedCorners = new();
    private readonly List<(int idx, Surface surf)> _removedSurfaces = new();

    public DeleteVertexCommand(Sector sector, Vertex vertex) { _sector = sector; _vertex = vertex; }

    public string Name => "Delete vertex";

    public void Apply()
    {
        _removedCorners.Clear();
        _removedSurfaces.Clear();

        foreach (var surf in _sector.Surfaces)
            for (int c = surf.Corners.Count - 1; c >= 0; c--)
                if (surf.Corners[c].Vertex == _vertex)
                {
                    _removedCorners.Add((surf, c, surf.Corners[c]));
                    surf.Corners.RemoveAt(c);
                }

        for (int s = _sector.Surfaces.Count - 1; s >= 0; s--)
            if (_sector.Surfaces[s].Corners.Count < 3)
            {
                _removedSurfaces.Add((s, _sector.Surfaces[s]));
                _sector.Surfaces.RemoveAt(s);
            }

        _vertexIndex = _sector.Vertices.IndexOf(_vertex);
        if (_vertexIndex >= 0) _sector.Vertices.RemoveAt(_vertexIndex);
    }

    public void Revert()
    {
        if (_vertexIndex >= 0) _sector.Vertices.Insert(System.Math.Min(_vertexIndex, _sector.Vertices.Count), _vertex);

        // Surfaces were removed high-index-first; re-insert low-index-first.
        foreach (var (idx, surf) in Enumerable.Reverse(_removedSurfaces))
            _sector.Surfaces.Insert(System.Math.Min(idx, _sector.Surfaces.Count), surf);

        // Corners were removed high-index-first; re-insert low-index-first.
        foreach (var (surf, idx, corner) in Enumerable.Reverse(_removedCorners))
            surf.Corners.Insert(System.Math.Min(idx, surf.Corners.Count), corner);
    }
}

/// <summary>Inserts a vertex at the midpoint of a surface edge (subdivides it). Reversible.</summary>
public sealed class InsertSurfaceVertexCommand : IEditCommand
{
    private readonly Sector _sector;
    private readonly Surface _surface;
    private readonly int _edge;
    private Vertex? _newVertex;
    private Surface.Corner? _newCorner;
    private int _insertAt;

    public InsertSurfaceVertexCommand(Surface surface, int edge)
    {
        _surface = surface;
        _sector = surface.Sector;
        _edge = edge;
    }

    public string Name => "Insert vertex";

    public void Apply()
    {
        int n = _surface.Corners.Count;
        var a = _surface.Corners[_edge % n];
        var b = _surface.Corners[(_edge + 1) % n];
        if (_newVertex is null)
        {
            var mid = (a.Vertex.Position + b.Vertex.Position) * 0.5;
            _newVertex = new Vertex(mid) { Sector = _sector };
            _newCorner = new Surface.Corner
            {
                Vertex = _newVertex,
                Uv = new TexVertex((a.Uv.U + b.Uv.U) * 0.5, (a.Uv.V + b.Uv.V) * 0.5),
                Intensity = (a.Intensity + b.Intensity) * 0.5f,
                MonoIntensity = a.MonoIntensity is null && b.MonoIntensity is null ? null :
                    ((a.MonoIntensity ?? a.Intensity.R) + (b.MonoIntensity ?? b.Intensity.R)) * 0.5f,
            };
        }
        _sector.Vertices.Add(_newVertex);
        _insertAt = _edge + 1;
        _surface.Corners.Insert(_insertAt, _newCorner!);
    }

    public void Revert()
    {
        _surface.Corners.RemoveAt(_insertAt);
        _sector.Vertices.Remove(_newVertex!);
    }
}

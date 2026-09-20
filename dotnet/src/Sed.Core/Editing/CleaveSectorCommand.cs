using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Splits a whole sector by a plane into two adjoined sectors
/// (`CleaveSector` in `LEV_UTILS.PAS`).
///
/// The steps, in order:
/// 1. classify every vertex against the plane (behind / on / in front);
/// 2. split each surface that straddles the plane, so every surface ends up
///    wholly on one side (or lying in the plane);
/// 3. collect the cut edges — surface edges whose two endpoints both landed
///    *on* the plane — and chain them into the cross-section polygon;
/// 4. move the behind-side surfaces and vertices into a new sector;
/// 5. cap the opening with a mirrored pair of surfaces, adjoined to each other,
///    one in each sector.
///
/// This mutates a lot of shared state — surfaces change owner, vertices move
/// between sectors, corner lists grow — so rather than inverting each step it
/// snapshots the affected topology before and after and swaps between them.
/// That keeps object identity stable across undo/redo, which matters because
/// adjoin partners in *other* sectors point at these surfaces.
/// </summary>
public sealed class CleaveSectorCommand : IEditCommand
{
    private const double OnPlaneEpsilon = 1e-6;

    private readonly Level _level;
    private readonly Sector _sector;
    private readonly Vec3 _normal;
    private readonly Vec3 _point;

    private Snapshot? _before;
    private Snapshot? _after;

    public CleaveSectorCommand(Level level, Sector sector, Vec3 planeNormal, Vec3 planePoint)
    {
        _level = level;
        _sector = sector;
        _normal = planeNormal.Normalized();
        _point = planePoint;
    }

    public string Name => "Cleave sector";

    /// <summary>False when the plane missed the sector, leaving it unchanged.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>Why the cleave left the level unchanged; null after a successful cleave.</summary>
    public string? FailureReason { get; private set; }
    /// <summary>A valid plane missed the sector, so compound operations can skip this cut.</summary>
    public bool PlaneMissed { get; private set; }

    /// <summary>The sector holding the behind-plane half, once the cleave has run.</summary>
    public Sector? NewSector { get; private set; }

    public void Apply()
    {
        if (_after is not null) { _after.Restore(); return; }
        FailureReason = null;
        PlaneMissed = false;
        NewSector = null;
        Succeeded = false;
        if (!CheckInput()) return;
        try
        {
            _before = Snapshot.Capture(_level, _sector);
            Succeeded = Build();
            if (Succeeded) _after = Snapshot.Capture(_level, _sector, NewSector);
        }
        catch (Exception ex)
        {
            Succeeded = false;
            FailureReason = "The cleave could not be completed: " + ex.Message;
        }
        if (!Succeeded)
        {
            _before?.Restore();
            _before = null;
            NewSector = null;
            FailureReason ??= "The plane does not form a closed cut through this sector.";
        }
    }

    public void Revert()
    {
        if (!Succeeded) return;
        _before?.Restore();
    }

    private bool Build()
    {
        // 1. Which side of the plane is each vertex on?
        var marks = new Dictionary<Vertex, int>();
        foreach (var v in _sector.Vertices)
            marks[v] = GeometryOps.ClassifyPoint(v.Position, _normal, _point, OnPlaneEpsilon);

        if (!marks.Values.Any(m => m < 0) || !marks.Values.Any(m => m > 0))
        {
            PlaneMissed = true;
            return Fail("The plane does not cross the sector.");
        }

        // Splitting just one side of a portal corrupts its geometry even if the
        // reverse pointer still matches. Refuse before making any changes.
        foreach (var surface in _sector.Surfaces)
            if (surface.Adjoin is not null && surface.Corners.Any(c => marks[c.Vertex] < 0)
                && surface.Corners.Any(c => marks[c.Vertex] > 0))
                return Fail("The cut crosses an existing portal. Splitting both portal sides together is not supported; choose a plane that leaves each portal intact.");

        // 2. Split straddling surfaces so each lies wholly on one side.
        foreach (var surf in _sector.Surfaces.ToList())
            SplitSurface(surf, marks);

        // 3. Chain the on-plane edges into the cross-section outline.
        var outline = BuildCrossSection(marks);
        if (outline.Count < 3) return Fail("The cross-section is open, branched or contains multiple loops; no geometry was changed.");
        int maxCorners = _level.Kind == ProjectType.InfernalMachine ? 64 : 24;
        if (outline.Count > maxCorners || _sector.Surfaces.Any(s => s.Corners.Count > maxCorners))
            return Fail($"The cleave would exceed the engine's {maxCorners}-vertex surface limit.");

        // 4. Move the behind half into a new sector.
        var newSector = _level.NewSector();
        newSector.Flags = _sector.Flags;
        newSector.Ambient = _sector.Ambient;
        newSector.ExtraLight = _sector.ExtraLight;
        newSector.Tint = _sector.Tint;
        newSector.ColorMap = _sector.ColorMap;
        newSector.ColorMapIndex = _sector.ColorMapIndex;
        newSector.Sound = _sector.Sound;
        newSector.SoundVolume = _sector.SoundVolume;
        newSector.Thrust = _sector.Thrust;
        newSector.Layer = _sector.Layer;

        foreach (var surf in _sector.Surfaces.ToList())
        {
            int side = SideOf(surf, marks);
            bool moves = side < 0 || (side == 0 && _normal.Dot(surf.Normal) < 0);
            if (!moves) continue;

            _sector.Surfaces.Remove(surf);
            surf.Sector = newSector;
            newSector.Surfaces.Add(surf);
        }

        foreach (var v in _sector.Vertices.ToList())
        {
            if (marks.GetValueOrDefault(v) >= 0) continue;
            _sector.Vertices.Remove(v);
            v.Sector = newSector;
            newSector.Vertices.Add(v);
        }

        // 5. Cap both halves with a mirrored, adjoined pair.
        var template = _sector.Surfaces.FirstOrDefault() ?? newSector.Surfaces.FirstOrDefault();

        var front = new Surface(_sector);
        foreach (var v in outline)
            front.Corners.Add(new Surface.Corner { Vertex = v, Intensity = ColorF.White });
        CopyProps(template, front);
        front.RecalcNormal();

        var back = new Surface(newSector);
        for (int i = outline.Count - 1; i >= 0; i--)
            back.Corners.Add(new Surface.Corner { Vertex = outline[i], Intensity = ColorF.White });
        CopyProps(template, back);
        back.RecalcNormal();

        // Whichever cap faces along the cleave normal belongs to the front half.
        if (_normal.Dot(front.Normal) <= 0)
        {
            (front, back) = (back, front);
            front.Sector = _sector;
            back.Sector = newSector;
        }

        _sector.Surfaces.Add(front);
        newSector.Surfaces.Add(back);

        front.Adjoin = back;
        back.Adjoin = front;
        front.AdjoinFlags = AdjoinFlags.Visible | AdjoinFlags.Move;
        back.AdjoinFlags = AdjoinFlags.Visible | AdjoinFlags.Move;

        // The new sector's surfaces still reference on-plane vertices owned by the
        // original sector; give it its own copies so the two are independent.
        var localCopies = new Dictionary<Vertex, Vertex>();
        foreach (var surf in newSector.Surfaces)
            for (int i = 0; i < surf.Corners.Count; i++)
            {
                var v = surf.Corners[i].Vertex;
                if (v.Sector == newSector) continue;

                if (!localCopies.TryGetValue(v, out var copy))
                    localCopies[v] = copy = newSector.AddVertex(v.Position);
                surf.Corners[i].Vertex = copy;
            }

        foreach (var thing in _level.Things)
            if (thing.Sector == _sector && GeometryOps.ClassifyPoint(thing.Position, _normal, _point, OnPlaneEpsilon) < 0)
                thing.Sector = newSector;
        // COLLIDEBOX is an inside-sector shortcut (sithIntersect.c), so a box
        // spanning the new dividing plane is unsafe. Fall back to face tests.
        _sector.HasCollideBox = false;
        newSector.HasCollideBox = false;
        _sector.Flags &= ~0x1000L;
        newSector.Flags &= ~0x1000L;
        _level.RenumberSectors();
        _sector.Renumber();
        newSector.Renumber();

        NewSector = newSector;
        return true;
    }

    private bool Fail(string reason)
    {
        FailureReason = reason;
        return false;
    }

    private bool CheckInput()
    {
        if (!Finite(_normal) || !Finite(_point) || _normal.LengthSquared < 0.99)
            return Fail("Use a finite plane with a nonzero normal.");
        if (!_level.Sectors.Contains(_sector) || _sector.Level != _level)
            return Fail("The sector does not belong to this level.");
        if (_sector.Vertices.Count < 4 || _sector.Vertices.Any(v => v is null || !Finite(v.Position)))
            return Fail("The sector has missing or non-finite vertices.");
        var vertices = _sector.Vertices.ToHashSet();
        if (_sector.Surfaces.Any(s => s is null || s.Sector != _sector || s.Corners.Count < 3
                || s.Corners.Any(c => c is null || c.Vertex is null || !vertices.Contains(c.Vertex))))
            return Fail("The sector contains a missing surface or a vertex reference outside its owning sector.");
        var surfaces = _level.Sectors.Where(s => s is not null).SelectMany(s => s.Surfaces).ToHashSet();
        if (_sector.Surfaces.Any(s => s.Adjoin is not null && (!surfaces.Contains(s.Adjoin) || s.Adjoin == s || s.Adjoin.Adjoin != s)))
            return Fail("Repair the sector's existing adjoin links before cleaving it.");
        if (_level.Things.Any(t => t is null || (t.Sector == _sector && !Finite(t.Position))))
            return Fail("The level contains a missing thing or a non-finite thing position.");
        return true;
    }

    private static bool Finite(Vec3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    /// <summary>
    /// Splits one surface across the plane, leaving the front part in place and
    /// adding the back part to the same sector. New vertices are inserted at the
    /// crossings and classified as on-plane, which is what step 3 chains up.
    /// </summary>
    private void SplitSurface(Surface surf, Dictionary<Vertex, int> marks)
    {
        int n = surf.Corners.Count;
        if (n < 3) return;

        var side = new int[n];
        for (int i = 0; i < n; i++)
            side[i] = marks.GetValueOrDefault(surf.Corners[i].Vertex);

        if (!side.Any(s => s > 0) || !side.Any(s => s < 0)) return;   // doesn't straddle

        var front = new List<Surface.Corner>();
        var back = new List<Surface.Corner>();

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            var cur = surf.Corners[i];
            var nxt = surf.Corners[next];

            if (side[i] >= 0) front.Add(Copy(cur));
            if (side[i] <= 0) back.Add(Copy(cur));

            bool crosses = (side[i] > 0 && side[next] < 0) || (side[i] < 0 && side[next] > 0);
            if (!crosses) continue;

            var a = cur.Vertex.Position;
            var b = nxt.Vertex.Position;
            var dir = b - a;
            double denom = _normal.Dot(dir);
            if (System.Math.Abs(denom) < 1e-12) continue;

            double t = _normal.Dot(_point - a) / denom;
            var cut = CutVertex(a + dir * t, marks);

            var uv = new TexVertex(
                cur.Uv.U + (nxt.Uv.U - cur.Uv.U) * t,
                cur.Uv.V + (nxt.Uv.V - cur.Uv.V) * t);
            float ft = (float)t;
            var light = new ColorF(
                cur.Intensity.R + (nxt.Intensity.R - cur.Intensity.R) * ft,
                cur.Intensity.G + (nxt.Intensity.G - cur.Intensity.G) * ft,
                cur.Intensity.B + (nxt.Intensity.B - cur.Intensity.B) * ft);
            float? mono = cur.MonoIntensity is not null || nxt.MonoIntensity is not null
                ? (cur.MonoIntensity ?? cur.Intensity.R) + ((nxt.MonoIntensity ?? nxt.Intensity.R) - (cur.MonoIntensity ?? cur.Intensity.R)) * ft
                : null;
            front.Add(new Surface.Corner { Vertex = cut, Uv = uv, Intensity = light, MonoIntensity = mono });
            back.Add(new Surface.Corner { Vertex = cut, Uv = uv, Intensity = light, MonoIntensity = mono });
        }

        if (front.Count < 3 || back.Count < 3)
            throw new InvalidOperationException("A straddling surface produced a degenerate half.");

        surf.Corners.Clear();
        surf.Corners.AddRange(front);
        surf.RecalcNormal();

        var backSurf = new Surface(_sector);
        backSurf.Corners.AddRange(back);
        CopyProps(surf, backSurf);
        backSurf.RecalcNormal();
        _sector.Surfaces.Add(backSurf);
    }

    /// <summary>
    /// Returns the on-plane vertex at a cut position, creating it only if the
    /// sector has none there yet.
    ///
    /// Welding matters: neighbouring faces cut the plane at the same corner, and
    /// the cross-section is chained by vertex *identity*. Minting a fresh vertex
    /// per face would leave four disconnected stubs instead of a closed loop, and
    /// the cleave would silently report failure.
    /// </summary>
    private Vertex CutVertex(Vec3 position, Dictionary<Vertex, int> marks)
    {
        int existing = _sector.FindVertex(position, WeldEpsilonSquared);
        if (existing >= 0)
        {
            var found = _sector.Vertices[existing];
            marks[found] = 0;
            return found;
        }

        var created = _sector.AddVertex(position);
        marks[created] = 0;
        return created;
    }

    /// <summary>Squared distance below which two cut points are the same vertex.</summary>
    private const double WeldEpsilonSquared = 1e-12;

    /// <summary>
    /// Gathers edges whose endpoints both lie on the plane and walks them into a
    /// closed loop — the shape of the cut, which becomes the capping surfaces.
    /// </summary>
    private List<Vertex> BuildCrossSection(Dictionary<Vertex, int> marks)
    {
        var edges = new List<(Vertex a, Vertex b)>();

        foreach (var surf in _sector.Surfaces)
        {
            // Skip surfaces lying entirely in the plane: every edge would qualify.
            if (surf.Corners.All(c => marks.GetValueOrDefault(c.Vertex) == 0)) continue;

            int n = surf.Corners.Count;
            for (int i = 0; i < n; i++)
            {
                var a = surf.Corners[i].Vertex;
                var b = surf.Corners[(i + 1) % n].Vertex;
                if (marks.GetValueOrDefault(a) != 0 || marks.GetValueOrDefault(b) != 0) continue;
                if (ReferenceEquals(a, b)) continue;

                bool known = edges.Any(e =>
                    (ReferenceEquals(e.a, a) && ReferenceEquals(e.b, b)) ||
                    (ReferenceEquals(e.a, b) && ReferenceEquals(e.b, a)));
                if (!known) edges.Add((a, b));
            }
        }

        if (edges.Count < 3) return new List<Vertex>();
        var degree = new Dictionary<Vertex, int>();
        foreach (var (a, b) in edges)
        {
            degree[a] = degree.GetValueOrDefault(a) + 1;
            degree[b] = degree.GetValueOrDefault(b) + 1;
        }
        if (degree.Values.Any(n => n != 2)) return new List<Vertex>();

        var loop = new List<Vertex> { edges[0].a, edges[0].b };
        var tail = edges[0].b;
        edges.RemoveAt(0);

        while (edges.Count > 0)
        {
            int found = -1;
            for (int i = 0; i < edges.Count; i++)
            {
                if (ReferenceEquals(edges[i].a, tail)) { tail = edges[i].b; found = i; break; }
                if (ReferenceEquals(edges[i].b, tail)) { tail = edges[i].a; found = i; break; }
            }
            if (found < 0) return new List<Vertex>();
            loop.Add(tail);
            edges.RemoveAt(found);
            if (ReferenceEquals(tail, loop[0])) break;
        }

        if (edges.Count != 0 || !ReferenceEquals(loop[^1], loop[0])) return new List<Vertex>();
        loop.RemoveAt(loop.Count - 1);
        return loop;
    }

    private static int SideOf(Surface surf, Dictionary<Vertex, int> marks)
    {
        foreach (var c in surf.Corners)
        {
            int m = marks.GetValueOrDefault(c.Vertex);
            if (m != 0) return m;
        }
        return 0;
    }

    private static Surface.Corner Copy(Surface.Corner c) =>
        new() { Vertex = c.Vertex, Uv = c.Uv, Intensity = c.Intensity, MonoIntensity = c.MonoIntensity };

    private static void CopyProps(Surface? from, Surface to)
    {
        if (from is null) return;
        to.Material = from.Material;
        to.MaterialIndex = from.MaterialIndex;
        to.SurfFlags = from.SurfFlags;
        to.FaceFlags = from.FaceFlags;
        to.Geo = from.Geo;
        to.Light = from.Light;
        to.Tex = from.Tex;
        to.ExtraLightIntensity = from.ExtraLightIntensity;
        to.UScale = from.UScale;
        to.VScale = from.VScale;
    }

    /// <summary>
    /// A restorable picture of the sectors a cleave touches: which sectors the
    /// level holds, what each affected sector owns, and each surface's corners,
    /// owner and adjoin. Restoring writes all of it back in place, so object
    /// identity survives undo and redo.
    /// </summary>
    private sealed class Snapshot
    {
        private readonly Level _level;
        private readonly List<Sector> _sectors;
        private readonly List<(Sector sector, List<Surface> surfaces, List<Vertex> vertices)> _contents = new();
        private readonly List<(Surface surface, Sector owner, List<Surface.Corner> corners,
            Surface? adjoin, long adjoinFlags)> _surfaces = new();
        private readonly Dictionary<Vertex, Sector?> _vertexOwners = new();
        private readonly List<(Thing thing, Sector? sector)> _thingSectors;
        private readonly List<(Sector sector, long flags, bool hasBox, Box box)> _collisionBoxes = new();

        private Snapshot(Level level)
        {
            _level = level;
            _sectors = level.Sectors.ToList();
            _thingSectors = level.Things.Select(t => (t, t.Sector)).ToList();
        }

        public static Snapshot Capture(Level level, params Sector?[] sectors)
        {
            var snapshot = new Snapshot(level);
            var seen = new HashSet<Sector>();

            foreach (var sector in sectors)
            {
                if (sector is null || !seen.Add(sector)) continue;
                snapshot._contents.Add((sector, sector.Surfaces.ToList(), sector.Vertices.ToList()));
                snapshot._collisionBoxes.Add((sector, sector.Flags, sector.HasCollideBox, sector.CollideBox));
                foreach (var vertex in sector.Vertices) snapshot._vertexOwners[vertex] = vertex.Sector;

                foreach (var surf in sector.Surfaces)
                {
                    snapshot.Record(surf);
                    // An adjoin partner in another sector has its link rewritten too.
                    if (surf.Adjoin is { } partner && !ReferenceEquals(partner.Sector, sector))
                        snapshot.Record(partner);
                }
            }

            return snapshot;
        }

        private void Record(Surface surf) =>
            _surfaces.Add((surf, surf.Sector, surf.Corners.Select(Copy).ToList(), surf.Adjoin, surf.AdjoinFlags));

        public void Restore()
        {
            _level.Sectors.Clear();
            _level.Sectors.AddRange(_sectors);
            foreach (var (vertex, owner) in _vertexOwners) vertex.Sector = owner;
            foreach (var (thing, sector) in _thingSectors) thing.Sector = sector;
            foreach (var (sector, flags, hasBox, box) in _collisionBoxes)
            {
                sector.Flags = flags;
                sector.HasCollideBox = hasBox;
                sector.CollideBox = box;
            }

            foreach (var (sector, surfaces, vertices) in _contents)
            {
                sector.Surfaces.Clear();
                sector.Surfaces.AddRange(surfaces);
                sector.Vertices.Clear();
                sector.Vertices.AddRange(vertices);
            }

            foreach (var (surf, owner, corners, adjoin, flags) in _surfaces)
            {
                surf.Sector = owner;
                surf.Corners.Clear();
                surf.Corners.AddRange(corners.Select(Copy));
                surf.Adjoin = adjoin;
                surf.AdjoinFlags = flags;
                surf.RecalcNormal();
            }

            _level.RenumberSectors();
            foreach (var (sector, _, _) in _contents) sector.Renumber();
        }
    }
}

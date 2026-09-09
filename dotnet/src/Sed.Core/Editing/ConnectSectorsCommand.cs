using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Joins two overlapping sectors into a portalled set (`ConnectSectors` in
/// `LEV_UTILS.PAS`).
///
/// Two sectors that overlap describe the same volume twice, which the engine
/// cannot render. Connecting them:
/// 1. cleaves each sector by every face plane of the other, so the overlap
///    becomes its own piece and the remainders split off as new sectors;
/// 2. adjoins each of the first sector's faces to a coincident face among those
///    new pieces, turning the shared boundaries into portals;
/// 3. deletes the second sector, whose remaining piece *is* the duplicated
///    overlap volume.
///
/// Built entirely from existing reversible commands — <see cref="CleaveSectorCommand"/>,
/// <see cref="MakeAdjoinCommand"/>, <see cref="DeleteSectorCommand"/> — so undo
/// is just replaying them backwards, and each step stays individually tested.
/// </summary>
public sealed class ConnectSectorsCommand : IEditCommand
{
    private readonly Level _level;
    private readonly Sector _a;
    private readonly Sector _b;

    private List<IEditCommand>? _steps;

    public ConnectSectorsCommand(Level level, Sector a, Sector b)
    {
        _level = level;
        _a = a;
        _b = b;
    }

    public string Name => "Connect sectors";

    /// <summary>Null when the connect succeeded, else why it could not be made.</summary>
    public string? Failure { get; private set; }

    /// <summary>How many face pairs became portals.</summary>
    public int PortalsCreated { get; private set; }

    /// <summary>Checks the preconditions without changing anything.</summary>
    public static string? Validate(Sector a, Sector b)
    {
        if (ReferenceEquals(a, b)) return "Pick two different sectors.";
        if (a.Surfaces.Count < 4 || b.Surfaces.Count < 4) return "Both sectors need to be closed volumes.";
        if (!GeometryOps.SectorsOverlap(a, b))
            return "The sectors do not overlap, so there is nothing to connect.";
        return null;
    }

    public void Apply()
    {
        if (_steps is not null)
        {
            foreach (var step in _steps) step.Apply();
            return;
        }

        _steps = new List<IEditCommand>();

        Failure = Validate(_a, _b);
        if (Failure is not null) return;

        var before = _level.Sectors.ToList();

        // Each sector is cleaved by the other's planes. The original object keeps
        // the piece inside every plane — the overlap — and the remainders split
        // off as new sectors.
        if (!CleaveBy(_b, _a) || !CleaveBy(_a, _b))
        {
            Revert();
            _steps.Clear();
            PortalsCreated = 0;
            return;
        }

        var pieces = _level.Sectors.Except(before).ToList();

        // Delete `b` first. It holds the duplicated overlap volume, and its faces
        // are still adjoined to its own cleave siblings; removing it frees those
        // siblings' faces so they can pair with `a` below. Running the adjoin pass
        // first would skip them as already-adjoined and leave one boundary open.
        foreach (var thing in _level.Things.Where(t => t.Sector == _b))
        {
            var move = new SetThingSectorCommand(thing, _a);
            move.Apply();
            _steps.Add(move);
        }
        var delete = new DeleteSectorCommand(_level, _b);
        delete.Apply();
        _steps.Add(delete);

        // Portal whatever boundaries are still open: each unadjoined face of `a`
        // that coincides with an unadjoined face of a new piece becomes a pair.
        foreach (var surf in _a.Surfaces.ToList())
        {
            if (surf.Adjoin is not null) continue;

            var partner = FindCoincident(surf, pieces);
            if (partner is null) continue;

            var adjoin = new MakeAdjoinCommand(surf, partner);
            adjoin.Apply();
            _steps.Add(adjoin);
            PortalsCreated++;
        }
    }

    public void Revert()
    {
        if (_steps is null) return;
        for (int i = _steps.Count - 1; i >= 0; i--) _steps[i].Revert();
    }

    /// <summary>Cleaves <paramref name="target"/> by every face plane of <paramref name="cutter"/>.</summary>
    private bool CleaveBy(Sector target, Sector cutter)
    {
        foreach (var face in cutter.Surfaces.ToList())
        {
            if (face.Corners.Count < 3) continue;
            face.RecalcNormal();
            if (face.Normal.LengthSquared < 0.5) continue;

            var cleave = new CleaveSectorCommand(
                _level, target, face.Normal, face.Corners[0].Vertex.Position);
            cleave.Apply();

            // A plane that missed leaves nothing to undo, so don't record it.
            if (cleave.Succeeded) _steps!.Add(cleave);
            else if (!cleave.PlaneMissed)
            {
                Failure = cleave.FailureReason ?? "A required sector cut could not be completed.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Finds an unadjoined face coincident with <paramref name="surf"/> among the
    /// pieces the cleaving produced. Only pieces still in the level are considered,
    /// since one of them may since have been deleted.
    /// </summary>
    private Surface? FindCoincident(Surface surf, List<Sector> pieces)
    {
        foreach (var piece in pieces)
        {
            if (!_level.Sectors.Contains(piece)) continue;

            foreach (var candidate in piece.Surfaces)
            {
                if (candidate.Adjoin is not null) continue;
                if (GeometryOps.SurfacesCoincide(surf, candidate)) return candidate;
            }
        }
        return null;
    }
}

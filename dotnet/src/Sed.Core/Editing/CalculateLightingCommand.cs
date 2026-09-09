using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Bakes static lighting over a set of sectors as one reversible edit. The whole
/// bake is a single undo step — a light calculation touches every vertex in the
/// level, and unwinding it one vertex at a time would be useless.
///
/// Undo restores every corner intensity and sector ambient exactly as they were,
/// so a bake can be tried and rejected without losing hand-authored lighting.
/// </summary>
public sealed class CalculateLightingCommand : IEditCommand
{
    private readonly Level _level;
    private readonly List<Sector> _targets;
    private readonly LightingOptions _options;

    private readonly List<Surface.Corner> _corners = new();
    private ColorF[]? _oldIntensities;
    private ColorF[]? _newIntensities;
    private float?[]? _oldMono;
    private float?[]? _newMono;
    private ColorF[]? _oldAmbients;
    private ColorF[]? _newAmbients;

    public CalculateLightingCommand(Level level, IEnumerable<Sector>? targets = null,
        LightingOptions? options = null)
    {
        _level = level;
        _targets = (targets ?? level.Sectors).Distinct().ToList();
        _options = options ?? new LightingOptions();
    }

    public string Name => _targets.Count == _level.Sectors.Count
        ? "Calculate lighting"
        : $"Calculate lighting ({_targets.Count} sectors)";

    /// <summary>Populated once the bake has run; null until then.</summary>
    public LightingStats? Stats { get; private set; }

    public void Apply()
    {
        if (_newIntensities is null)
        {
            Bake();
            return;
        }

        // Redo: replay the captured result rather than recomputing, so redo is
        // instant and cannot drift from what undo removed.
        for (int i = 0; i < _corners.Count; i++)
        { _corners[i].Intensity = _newIntensities[i]; _corners[i].MonoIntensity = _newMono![i]; }
        for (int i = 0; i < _targets.Count; i++) _targets[i].Ambient = _newAmbients![i];
    }

    public void Revert()
    {
        if (_oldIntensities is null) return;
        for (int i = 0; i < _corners.Count; i++)
        { _corners[i].Intensity = _oldIntensities[i]; _corners[i].MonoIntensity = _oldMono![i]; }
        for (int i = 0; i < _targets.Count; i++) _targets[i].Ambient = _oldAmbients![i];
    }

    private void Bake()
    {
        // Snapshot the corners in a stable order so old/new arrays line up.
        _corners.Clear();
        foreach (var sector in _targets)
            foreach (var surf in sector.Surfaces)
                foreach (var c in surf.Corners)
                    _corners.Add(c);

        _oldIntensities = _corners.Select(c => c.Intensity).ToArray();
        _oldMono = _corners.Select(c => c.MonoIntensity).ToArray();
        _oldAmbients = _targets.Select(s => s.Ambient).ToArray();

        try { Stats = LightCalculator.Calculate(_level, _targets, _options); }
        catch { Revert(); throw; }

        _newIntensities = _corners.Select(c => c.Intensity).ToArray();
        _newMono = _corners.Select(c => c.MonoIntensity).ToArray();
        _newAmbients = _targets.Select(s => s.Ambient).ToArray();
    }
}

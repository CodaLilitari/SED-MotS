using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>Sets a sector's ambient light (reversible).</summary>
public sealed class SetSectorAmbientCommand : IEditCommand
{
    private readonly Sector _sector;
    private readonly ColorF _old, _new;

    public SetSectorAmbientCommand(Sector sector, ColorF ambient)
    {
        _sector = sector; _old = sector.Ambient; _new = ambient;
    }

    public string Name => "Set sector light";
    public void Apply() => _sector.Ambient = _new;
    public void Revert() => _sector.Ambient = _old;

    /// <summary>Builds a command that brightens/darkens the sector's ambient by a grey delta.</summary>
    public static SetSectorAmbientCommand Adjust(Sector sector, float delta)
    {
        float v = System.Math.Clamp(sector.Ambient.R + delta, 0f, 1f);
        return new SetSectorAmbientCommand(sector, new ColorF(v, v, v));
    }
}

/// <summary>
/// Sets the light intensity of every surface corner that uses a given vertex
/// (a "vertex light"), capturing the previous values for undo.
/// </summary>
public sealed class SetVertexLightCommand : IEditCommand
{
    private readonly List<(Surface.Corner corner, ColorF old, float? mono)> _changes = new();
    private readonly ColorF _new;

    public SetVertexLightCommand(Sector sector, Vertex vertex, ColorF intensity)
    {
        _new = intensity;
        foreach (var surf in sector.Surfaces)
            foreach (var c in surf.Corners)
                if (c.Vertex == vertex)
                    _changes.Add((c, c.Intensity, c.MonoIntensity));
    }

    public string Name => "Set vertex light";
    public void Apply() { foreach (var (c, _, _) in _changes) { c.Intensity = _new; c.MonoIntensity = _new.R; } }
    public void Revert() { foreach (var (c, old, mono) in _changes) { c.Intensity = old; c.MonoIntensity = mono; } }

    /// <summary>Builds a command that brightens/darkens the vertex light by a grey delta.</summary>
    public static SetVertexLightCommand Adjust(Sector sector, Vertex vertex, float delta)
    {
        float cur = 0f;
        foreach (var surf in sector.Surfaces)
            foreach (var c in surf.Corners)
                if (c.Vertex == vertex) { cur = c.Intensity.R; break; }
        float v = System.Math.Clamp(cur + delta, 0f, 1f);
        return new SetVertexLightCommand(sector, vertex, new ColorF(v, v, v));
    }
}

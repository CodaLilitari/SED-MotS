using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

public enum VertexLightComponent { Mono, Red, Green, Blue }

/// <summary>Edits one component on corners sharing a vertex without flattening the other components.</summary>
public sealed class SetVertexLightComponentCommand : IEditCommand
{
    private readonly List<(Surface.Corner corner, ColorF color, float? mono)> _corners;
    private readonly VertexLightComponent _component;
    private readonly float _value;
    public SetVertexLightComponentCommand(Sector sector, Vertex vertex, VertexLightComponent component, float value)
    {
        if (!Enum.IsDefined(component) || !float.IsFinite(value) || value < 0 || value > 1000)
            throw new ArgumentException("Lighting component must be finite and between 0 and 1000.");
        if (sector.Level.Kind == ProjectType.JediKnight && component != VertexLightComponent.Mono)
            throw new ArgumentException("Independent RGB vertex components require a MotS level.");
        _component = component; _value = value;
        _corners = sector.Surfaces.SelectMany(s => s.Corners).Where(c => c.Vertex == vertex)
            .Select(c => (c, c.Intensity, c.MonoIntensity)).ToList();
        _jk = sector.Level.Kind == ProjectType.JediKnight;
    }
    private readonly bool _jk;
    public string Name => "Set vertex " + _component.ToString().ToLowerInvariant() + " light";
    public void Apply()
    {
        foreach (var (corner, color, mono) in _corners)
        {
            if (_component == VertexLightComponent.Mono)
            {
                corner.MonoIntensity = _value;
                if (_jk) corner.Intensity = new ColorF(_value, _value, _value);
            }
            else
            {
                // Null means the authored scalar was derived from the old red
                // channel. Freeze that value before changing RGB independently.
                corner.MonoIntensity = mono ?? color.R;
                corner.Intensity = new ColorF(_component == VertexLightComponent.Red ? _value : color.R,
                    _component == VertexLightComponent.Green ? _value : color.G,
                    _component == VertexLightComponent.Blue ? _value : color.B);
            }
        }
    }
    public void Revert()
    { foreach (var (corner, color, mono) in _corners) { corner.Intensity = color; corner.MonoIntensity = mono; } }
}

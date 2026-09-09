using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>Replace texel UVs in existing corner order without changing geometry or lighting.</summary>
public sealed class SetSurfaceUvCommand : IEditCommand
{
    private readonly Surface _surface;
    private readonly TexVertex[] _before, _after;
    public SetSurfaceUvCommand(Surface surface, IReadOnlyList<TexVertex> uv)
    {
        if (uv.Count != surface.Corners.Count) throw new ArgumentException("Supply one UV pair per corner.", nameof(uv));
        if (uv.Any(v => !double.IsFinite(v.U) || !double.IsFinite(v.V))) throw new ArgumentException("UV pairs must be finite.", nameof(uv));
        _surface = surface; _before = surface.Corners.Select(c => c.Uv).ToArray(); _after = uv.ToArray();
    }
    public string Name => "Set surface UVs";
    public void Apply() => Write(_after);
    public void Revert() => Write(_before);
    private void Write(TexVertex[] values)
    {
        if (_surface.Corners.Count != values.Length) throw new InvalidOperationException("Surface topology changed since the UV edit was prepared.");
        for (int i = 0; i < values.Length; i++) _surface.Corners[i].Uv = values[i];
    }
}

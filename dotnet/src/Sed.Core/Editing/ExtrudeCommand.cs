using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Extrudes a surface along its normal by a distance, creating a new sector
/// (adjoin surface, opposite surface, and side surfaces). Fully reversible.
/// </summary>
public sealed class ExtrudeSurfaceCommand : IEditCommand
{
    private const long AdjoinFlagMask = 0x01 | 0x02;

    private readonly Surface _surface;
    private readonly double _distance;

    private bool _built;
    private Sector? _newSector;
    private Surface? _adjoinSurface;
    private Surface? _oldAdjoin;
    private long _oldAdjoinFlags;

    public ExtrudeSurfaceCommand(Surface surface, double distance)
    {
        _surface = surface;
        _distance = distance;
    }

    public string Name => "Extrude surface";

    public void Apply()
    {
        var level = _surface.Sector.Level;

        if (!_built)
        {
            BuildExtrusion();
            _built = true;
        }
        else
        {
            if (!level.Sectors.Contains(_newSector!))
            {
                level.Sectors.Add(_newSector!);
                level.RenumberSectors();
            }
        }

        _surface.Adjoin = _adjoinSurface;
        _surface.AdjoinFlags = AdjoinFlagMask;
        if (_adjoinSurface != null)
        {
            _adjoinSurface.Adjoin = _surface;
            _adjoinSurface.AdjoinFlags = AdjoinFlagMask;
        }
    }

    public void Revert()
    {
        var level = _surface.Sector.Level;

        if (_newSector != null && level.Sectors.Contains(_newSector))
        {
            level.Sectors.Remove(_newSector);
            level.RenumberSectors();
        }

        _surface.Adjoin = _oldAdjoin;
        _surface.AdjoinFlags = _oldAdjoinFlags;

        if (_adjoinSurface != null)
        {
            _adjoinSurface.Adjoin = null;
            _adjoinSurface.AdjoinFlags = 0;
        }
    }

    private void BuildExtrusion()
    {
        var level = _surface.Sector.Level;
        var source = _surface.Sector;
        int n = _surface.Corners.Count;

        _oldAdjoin = _surface.Adjoin;
        _oldAdjoinFlags = _surface.AdjoinFlags;

        // 1. Create new sector, copy properties from source sector.
        _newSector = level.NewSector();
        _newSector.Ambient = source.Ambient;
        _newSector.Flags = source.Flags & ~0x1000L; // New volume has no inherited interior COLLIDEBOX.
        _newSector.ColorMap = source.ColorMap;
        _newSector.Tint = source.Tint;
        _newSector.ExtraLight = source.ExtraLight;
        _newSector.Sound = source.Sound;
        _newSector.SoundVolume = source.SoundVolume;
        _newSector.Thrust = source.Thrust;
        _newSector.Layer = source.Layer;

        _surface.RecalcNormal();
        var normal = _surface.Normal;

        // 2. Front ring: clone source vertex positions into new sector.
        var front = new Vertex[n];
        for (int i = 0; i < n; i++)
            front[i] = _newSector.AddVertex(_surface.Corners[i].Vertex.Position);

        // 3. Back ring: offset by -normal * distance.
        var back = new Vertex[n];
        for (int i = 0; i < n; i++)
            back[i] = _newSector.AddVertex(_surface.Corners[i].Vertex.Position - normal * _distance);

        // Adjoin surface — copy of the original using front ring (same winding/UVs).
        _adjoinSurface = NewSurfaceLike(_surface, _newSector);
        for (int i = 0; i < n; i++)
            _adjoinSurface.Corners.Add(new Surface.Corner
            {
                Vertex = front[i],
                Uv = _surface.Corners[i].Uv,
                Intensity = _surface.Corners[i].Intensity,
                MonoIntensity = _surface.Corners[i].MonoIntensity,
            });
        _adjoinSurface.RecalcNormal();

        // Opposite surface — reversed winding so it faces the adjoin.
        var opposite = NewSurfaceLike(_surface, _newSector);
        for (int i = n - 1; i >= 0; i--)
            opposite.Corners.Add(new Surface.Corner
            {
                Vertex = back[i],
                Uv = _surface.Corners[i].Uv,
                Intensity = ColorF.White,
            });
        opposite.RecalcNormal();

        // 4. Side surfaces (quads) connecting front and back rings.
        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            var side = NewSurfaceLike(_surface, _newSector);
            side.Corners.Add(new Surface.Corner { Vertex = front[i], Uv = new TexVertex(0, 0), Intensity = ColorF.White });
            side.Corners.Add(new Surface.Corner { Vertex = front[next], Uv = new TexVertex(64, 0), Intensity = ColorF.White });
            side.Corners.Add(new Surface.Corner { Vertex = back[next], Uv = new TexVertex(64, 64), Intensity = ColorF.White });
            side.Corners.Add(new Surface.Corner { Vertex = back[i], Uv = new TexVertex(0, 64), Intensity = ColorF.White });
            side.RecalcNormal();
        }
    }

    private static Surface NewSurfaceLike(Surface template, Sector owner)
    {
        var s = owner.NewSurface();
        s.Material = template.Material;
        s.MaterialIndex = template.MaterialIndex;
        s.SurfFlags = template.SurfFlags;
        s.FaceFlags = template.FaceFlags;
        s.Geo = template.Geo;
        s.Light = template.Light;
        s.Tex = template.Tex;
        s.ExtraLightIntensity = template.ExtraLightIntensity;
        s.UScale = template.UScale;
        s.VScale = template.VScale;
        return s;
    }
}

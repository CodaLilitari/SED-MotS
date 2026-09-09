using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Extrudes a surface outward, opposite its inward normal, creating a new sector
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
    private int _oldGeo;

    public ExtrudeSurfaceCommand(Surface surface, double distance)
    {
        _surface = surface;
        _distance = distance;
    }

    public string Name => "Extrude surface";

    /// <summary>Checks a finite positive extrusion without changing level topology.</summary>
    public static string? Validate(Surface surface, double distance)
    {
        if (!double.IsFinite(distance) || distance <= 0) return "Extrusion distance must be finite and positive; inward extrusion would overlap the source room.";
        if (!surface.Sector.Level.Sectors.Contains(surface.Sector) || !surface.Sector.Surfaces.Contains(surface))
            return "The source surface must belong to a live sector.";
        if (surface.Adjoin is not null) return "The source surface is already adjoined.";
        int maximum = surface.Sector.Level.Kind == ProjectType.InfernalMachine ? 64 : 24;
        if (surface.Corners.Count < 3 || surface.Corners.Count > maximum) return $"The source polygon must have between 3 and {maximum} corners.";
        if (surface.Corners.Any(c => c.Vertex is null || !surface.Sector.Vertices.Contains(c.Vertex) ||
            !double.IsFinite(c.Vertex.Position.X) || !double.IsFinite(c.Vertex.Position.Y) || !double.IsFinite(c.Vertex.Position.Z)))
            return "The source polygon has an invalid vertex.";
        surface.RecalcNormal();
        if (surface.Normal.LengthSquared < 0.99) return "The source polygon has no usable normal.";
        var origin = surface.Corners[0].Vertex.Position;
        foreach (var corner in surface.Corners)
            if (System.Math.Abs(surface.Normal.Dot(corner.Vertex.Position - origin)) > 1e-7)
                return "The source polygon must be planar.";
        for (int i = 0; i < surface.Corners.Count; i++)
        {
            var a = surface.Corners[i].Vertex.Position;
            var edge = surface.Corners[(i + 1) % surface.Corners.Count].Vertex.Position - a;
            if (edge.LengthSquared <= 1e-18) return "The source polygon has a zero-length edge.";
            foreach (var corner in surface.Corners)
                if (edge.Cross(corner.Vertex.Position - a).Dot(surface.Normal) < -1e-7)
                    return "The source polygon must be convex and consistently wound.";
            var offset = a - surface.Normal * distance;
            if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || !double.IsFinite(offset.Z))
                return "Extrusion would produce a non-finite coordinate.";
        }
        return null;
    }

    public void Apply()
    {
        var level = _surface.Sector.Level;

        if (!_built)
        {
            if (Validate(_surface, _distance) is { } problem) throw new ArgumentException(problem);
            BuildExtrusion();
            _built = true;
            level.Sectors.Add(_newSector!);
            level.RenumberSectors();
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
        _surface.Geo = 0; // An open portal must not retain an opaque wall face.
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
        _surface.Geo = _oldGeo;

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
        _oldGeo = _surface.Geo;

        // 1. Create new sector, copy properties from source sector.
        // Build detached so a failed allocation/geometry step cannot append a partial sector.
        _newSector = new Sector(level);
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

        // Front cap faces into the new volume, opposite the source face.
        _adjoinSurface = NewSurfaceLike(_surface, _newSector);
        _adjoinSurface.Geo = 0;
        for (int i = n - 1; i >= 0; i--)
            _adjoinSurface.Corners.Add(new Surface.Corner
            {
                Vertex = front[i],
                Uv = _surface.Corners[i].Uv,
                Intensity = _surface.Corners[i].Intensity,
                MonoIntensity = _surface.Corners[i].MonoIntensity,
            });
        _adjoinSurface.RecalcNormal();

        // The far cap has the source winding and faces back toward the portal.
        var opposite = NewSurfaceLike(_surface, _newSector);
        for (int i = 0; i < n; i++)
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

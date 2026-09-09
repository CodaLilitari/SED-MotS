using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Cleaves (splits) a surface by a plane into two surfaces. Vertices on the
/// front side (mark &gt;= 0) form a new surface; vertices on the back side
/// (mark &lt;= 0) remain in the original. Edge crossings produce interpolated
/// vertices shared by both surfaces. Fully reversible; a plane that does not
/// intersect the surface is a no-op.
/// </summary>
public sealed class CleaveSurfaceCommand : IEditCommand
{
    private readonly Surface _surface;
    private readonly Vec3 _planeNormal;
    private readonly Vec3 _planePoint;

    private bool _built;
    private List<Surface.Corner>? _originalCorners;
    private List<Surface.Corner>? _backCorners;
    private List<Surface.Corner>? _frontCorners;
    private List<Vertex>? _insertedVertices;
    private Surface? _newSurface;

    public CleaveSurfaceCommand(Surface surface, Vec3 planeNormal, Vec3 planePoint)
    {
        _surface = surface;
        _planeNormal = planeNormal;
        _planePoint = planePoint;
    }

    public string Name => "Cleave surface";
    public string? FailureReason { get; private set; }

    public void Apply()
    {
        var sector = _surface.Sector;

        if (!_built)
        {
            _originalCorners = _surface.Corners.ToList();
            BuildSplit(sector);
            _built = true;
        }

        if (_insertedVertices != null)
            foreach (var v in _insertedVertices)
                sector.Vertices.Add(v);

        if (_backCorners != null)
        {
            _surface.Corners.Clear();
            _surface.Corners.AddRange(_backCorners);
            _surface.RecalcNormal();
        }

        if (_newSurface != null && !sector.Surfaces.Contains(_newSurface))
            sector.Surfaces.Add(_newSurface);
    }

    public void Revert()
    {
        var sector = _surface.Sector;

        if (_newSurface != null)
            sector.Surfaces.Remove(_newSurface);

        if (_insertedVertices != null)
            foreach (var v in _insertedVertices)
                sector.Vertices.Remove(v);

        if (_backCorners != null && _originalCorners != null)
        {
            _surface.Corners.Clear();
            _surface.Corners.AddRange(_originalCorners);
            _surface.RecalcNormal();
        }
    }

    private void BuildSplit(Sector sector)
    {
        int n = _surface.Corners.Count;

        var marks = new int[n];
        for (int i = 0; i < n; i++)
            marks[i] = GeometryOps.ClassifyPoint(
                _surface.Corners[i].Vertex.Position, _planeNormal, _planePoint);

        bool hasFront = false, hasBack = false;
        for (int i = 0; i < n; i++)
        {
            if (marks[i] > 0) hasFront = true;
            if (marks[i] < 0) hasBack = true;
        }

        if (!hasFront || !hasBack)
            return;

        if (_surface.Adjoin is not null)
        {
            FailureReason = "The cut crosses a portal. Splitting both portal sides together is not supported; unadjoin deliberately or choose another surface.";
            return;
        }

        _frontCorners = new List<Surface.Corner>();
        _backCorners = new List<Surface.Corner>();
        _insertedVertices = new List<Vertex>();

        for (int i = 0; i < n; i++)
        {
            int next = (i + 1) % n;
            var cur = _surface.Corners[i];
            var nxt = _surface.Corners[next];
            int curMark = marks[i];
            int nextMark = marks[next];

            if (curMark >= 0)
                _frontCorners.Add(CopyCorner(cur));
            if (curMark <= 0)
                _backCorners.Add(CopyCorner(cur));

            bool crosses = (curMark > 0 && nextMark < 0) || (curMark < 0 && nextMark > 0);
            if (!crosses) continue;

            var a = cur.Vertex.Position;
            var b = nxt.Vertex.Position;
            var dir = b - a;
            double denom = _planeNormal.Dot(dir);
            if (System.Math.Abs(denom) < 1e-9) continue;

            double t = _planeNormal.Dot(_planePoint - a) / denom;
            var hitPos = a + dir * t;

            var newVert = new Vertex(hitPos) { Sector = sector };
            _insertedVertices.Add(newVert);

            double ft = t;
            var interpUv = new TexVertex(
                cur.Uv.U + (nxt.Uv.U - cur.Uv.U) * ft,
                cur.Uv.V + (nxt.Uv.V - cur.Uv.V) * ft);

            float ftf = (float)ft;
            var interpIntensity = new ColorF(
                cur.Intensity.R + (nxt.Intensity.R - cur.Intensity.R) * ftf,
                cur.Intensity.G + (nxt.Intensity.G - cur.Intensity.G) * ftf,
                cur.Intensity.B + (nxt.Intensity.B - cur.Intensity.B) * ftf);
            float? mono = cur.MonoIntensity is null && nxt.MonoIntensity is null ? null :
                (cur.MonoIntensity ?? cur.Intensity.R) +
                ((nxt.MonoIntensity ?? nxt.Intensity.R) - (cur.MonoIntensity ?? cur.Intensity.R)) * ftf;

            _frontCorners.Add(new Surface.Corner { Vertex = newVert, Uv = interpUv, Intensity = interpIntensity, MonoIntensity = mono });
            _backCorners.Add(new Surface.Corner { Vertex = newVert, Uv = interpUv, Intensity = interpIntensity, MonoIntensity = mono });
        }

        _newSurface = new Surface(sector);
        CopySurfaceProps(_surface, _newSurface);
        _newSurface.Corners.AddRange(_frontCorners);
        _newSurface.RecalcNormal();
    }

    private static Surface.Corner CopyCorner(Surface.Corner src) => new()
    {
        Vertex = src.Vertex,
        Uv = src.Uv,
        Intensity = src.Intensity,
        MonoIntensity = src.MonoIntensity,
    };

    private static void CopySurfaceProps(Surface src, Surface dst)
    {
        dst.Material = src.Material;
        dst.MaterialIndex = src.MaterialIndex;
        dst.SurfFlags = src.SurfFlags;
        dst.FaceFlags = src.FaceFlags;
        dst.Geo = src.Geo;
        dst.Light = src.Light;
        dst.Tex = src.Tex;
        dst.ExtraLightIntensity = src.ExtraLightIntensity;
        dst.UScale = src.UScale;
        dst.VScale = src.VScale;
    }
}

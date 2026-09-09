using Sed.Core.Math;

namespace Sed.Formats.ThreeDo;

/// <summary>A polygon face within a 3DO mesh: material index + per-corner vertex/UV indices.</summary>
public sealed class Face3do
{
    public int Material = -1;

    /// <summary>Engine per-face fields, preserved verbatim on write (surfflags, geo, light, tex, extralight).</summary>
    public long FaceFlags;
    public int Geo = 4;
    public int Light = 3;
    public int Tex = 3;
    public float ExtraLight;

    public List<int> VertexIndices { get; } = new();
    public List<int> UvIndices { get; } = new();
}

/// <summary>One mesh of a 3DO geoset: local-space vertices, texture vertices, and faces.</summary>
public sealed class Mesh3do
{
    public string Name = string.Empty;
    public int GeometryMode = 4;
    public int LightingMode = 3;
    public int TextureMode = 3;
    public List<Vec3> Vertices { get; } = new();
    /// <summary>Scalar base light stored as the fourth component of each 3DO vertex.</summary>
    public List<float> VertexIntensities { get; } = new();
    public List<Vec3> VertexNormals { get; } = new();
    public List<Vec2> Uvs { get; } = new();
    public List<Face3do> Faces { get; } = new();
}

/// <summary>A hierarchy node: which mesh it draws and its rest transform relative to its parent.</summary>
public sealed class HierarchyNode
{
    public int Mesh = -1;
    public int Parent = -1;
    public Vec3 Offset;
    public double Pitch, Yaw, Roll;
}

/// <summary>
/// A parsed Sith-engine 3DO model (highest-detail geoset). Materials are MAT
/// names resolved against the resource GOBs; the hierarchy positions meshes.
/// </summary>
public sealed class ThreeDoModel
{
    public string Name { get; set; } = string.Empty;
    public double Version { get; set; }
    /// <summary>Windows Delphi native-project normalized model fingerprint, when source encoding is verifiable.</summary>
    public string? ArchitectureContentHash { get; set; }
    public Vec3 InsertOffset { get; set; }
    public List<string> Materials { get; } = new();
    public List<Mesh3do> Meshes { get; } = new();
    public List<HierarchyNode> Nodes { get; } = new();

    public string GetMaterial(int index) =>
        (uint)index < (uint)Materials.Count ? Materials[index] : string.Empty;
}

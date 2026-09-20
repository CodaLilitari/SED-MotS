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
    /// <summary>Engine node flags and type bitmasks, stored as hexadecimal 32-bit fields.</summary>
    public uint Flags;
    public uint Type = 1;
    /// <summary>Authored hierarchy name; independent of the mesh name and retained with its original case.</summary>
    public string Name = string.Empty;
    public int Mesh = -1;
    public int Parent = -1;
    public Vec3 Offset;
    public double Pitch, Yaw, Roll;
    /// <summary>Authored local pivot. Preserved by format I/O; rendering support is separate.</summary>
    public Vec3 Pivot;
}

/// <summary>
/// A parsed Sith-engine 3DO model (highest-detail geoset). Materials are MAT
/// names resolved against the resource GOBs; the hierarchy positions meshes.
/// </summary>
public sealed class ThreeDoModel
{
    public string Name { get; set; } = string.Empty;
    public double Version { get; set; }
    /// <summary>Exact captured source-byte SHA256; absent for a model parsed only from decoded text.</summary>
    public string? RawSha256 { get; set; }
    /// <summary>Windows Delphi native-project normalized model fingerprint, when source encoding is verifiable.</summary>
    public string? ArchitectureContentHash { get; set; }
    /// <summary>All-LOD shape metadata captured from source; null is unverified, not a single-LOD assertion.</summary>
    public ThreeDoTopology? ArchitectureTopology { get; set; }
    public string? ArchitectureTopologyError { get; set; }
    public Vec3 InsertOffset { get; set; }
    public List<string> Materials { get; } = new();
    public List<Mesh3do> Meshes { get; } = new();
    public List<HierarchyNode> Nodes { get; } = new();

    public string GetMaterial(int index) =>
        (uint)index < (uint)Materials.Count ? Materials[index] : string.Empty;
}

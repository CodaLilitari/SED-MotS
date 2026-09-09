using Sed.Core.Math;
using System.Runtime.InteropServices;

namespace Sed.Rendering;

/// <summary>A GPU-ready vertex with independent RGB and monochrome light (interleaved float32).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MeshVertex
{
    public float Px, Py, Pz;
    public float Nx, Ny, Nz;
    public float R, G, B;
    public float U, V;
    public float Mono;

    public MeshVertex(Vec3 p, Vec3 n, ColorF c, double u = 0, double v = 0, float? mono = null)
    {
        Px = (float)p.X; Py = (float)p.Y; Pz = (float)p.Z;
        Nx = (float)n.X; Ny = (float)n.Y; Nz = (float)n.Z;
        R = c.R; G = c.G; B = c.B;
        U = (float)u; V = (float)v;
        Mono = mono ?? c.R;
    }

    public const uint Stride = 12 * sizeof(float);
    public const uint MonoOffset = 11 * sizeof(float);
}

/// <summary>An indexed triangle mesh produced from the level model.</summary>
public sealed class Mesh
{
    public List<MeshVertex> Vertices { get; } = new();
    public List<uint> Indices { get; } = new();

    public int TriangleCount => Indices.Count / 3;
    public bool IsEmpty => Indices.Count == 0;

    public void AddTriangle(MeshVertex a, MeshVertex b, MeshVertex c)
    {
        uint baseIndex = (uint)Vertices.Count;
        Vertices.Add(a);
        Vertices.Add(b);
        Vertices.Add(c);
        Indices.Add(baseIndex);
        Indices.Add(baseIndex + 1);
        Indices.Add(baseIndex + 2);
    }

    /// <summary>Appends another mesh's geometry, offsetting its indices.</summary>
    public void Append(Mesh other)
    {
        uint baseIndex = (uint)Vertices.Count;
        Vertices.AddRange(other.Vertices);
        foreach (var idx in other.Indices) Indices.Add(idx + baseIndex);
    }
}

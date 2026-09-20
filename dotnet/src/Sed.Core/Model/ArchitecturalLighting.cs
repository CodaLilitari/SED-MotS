namespace Sed.Core.Model;

/// <summary>
/// Current editor binding/bake provenance. Raw model resource identity and bake
/// inputs are separate from the optional historical normalized model hash.
/// This record alone never verifies that the current resources are available.
/// </summary>
public sealed record ArchitectureLightingProvenance(string Origin, string ModelName,
    string ModelSha256, string InputSha256, string SamplesSha256);

/// <summary>Four independent MotS baked model-light components, in engine file order.</summary>
public readonly record struct ArchitecturalLightVertex(double Mono, double Red, double Green, double Blue);

public sealed class ArchitecturalLightMesh
{
    public List<ArchitecturalLightVertex> Vertices { get; } = new();
}

/// <summary>Per-mesh baked lighting addressed by a placed thing's ArchLightingIndex.</summary>
public sealed class ArchitecturalLightObject
{
    public List<ArchitecturalLightMesh> Meshes { get; } = new();
    public ArchitecturalLightObject Clone()
    {
        var copy = new ArchitecturalLightObject();
        foreach (var mesh in Meshes)
        {
            var clonedMesh = new ArchitecturalLightMesh();
            clonedMesh.Vertices.AddRange(mesh.Vertices);
            copy.Meshes.Add(clonedMesh);
        }
        return copy;
    }
}

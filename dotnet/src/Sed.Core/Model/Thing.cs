using Sed.Core.Math;

namespace Sed.Core.Model;

/// <summary>A placed game object / entity (TThing).</summary>
public sealed class Thing
{
    public int Id;
    public int Num;
    public string Name = string.Empty;
    /// <summary>Template this thing instantiates (resolves its model3d, type, etc.).</summary>
    public string Template = string.Empty;

    public Vec3 Position;
    /// <summary>Pitch, yaw, roll in degrees (pch, yaw, rol).</summary>
    public double Pitch, Yaw, Roll;

    public int Layer;
    /// <summary>Index into Level.ArchitecturalLighting, or -1 when unbound.</summary>
    public int ArchLightingIndex = -1;
    /// <summary>Resolved model name at import. A changed model requires a new lighting bake.</summary>
    public string ArchLightingSourceModel = string.Empty;
    /// <summary>Optional source-content baseline retained from a native project; resource-aware verification is separate.</summary>
    public string ArchLightingSourceModelHash = string.Empty;
    /// <summary>Optional current editor binding/bake evidence; resource verification is separate.</summary>
    public ArchitectureLightingProvenance? ArchLightingProvenance;
    public Sector? Sector;
    public Box BBox;

    public Level? Level;

    /// <summary>Template-derived key/value parameters (TTPLValues).</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Ordered imported parameter records, including repeated frame= entries.</summary>
    public List<KeyValuePair<string, string>> SourceParameters { get; } = new();

    public ThingFlags Flags { get; set; }
}

using Sed.Core.Math;

namespace Sed.Core.Model;

/// <summary>An editor light source (TSedLight).</summary>
public sealed class Light
{
    public int Id;
    public int Num;
    public long Flags;
    public int Layer;

    public ColorF Color = ColorF.White;
    /// <summary>White/monochrome intensity; also supplies RGB intensity when no independent value is authored.</summary>
    public double Intensity = 1.0;
    /// <summary>Independent MotS RGB intensity. Null inherits <see cref="Intensity"/>; explicit zero disables RGB contribution.</summary>
    public double? RgbIntensity;
    public double EffectiveRgbIntensity => RgbIntensity ?? Intensity;
    public double Range;
    /// <summary>Preserved legacy MotS RGB-range metadata. Baking uses <see cref="Range"/> for both channels.</summary>
    public double? LegacyRgbRange;
    public Vec3 Position;
}

/// <summary>A COG script reference (TCOG).</summary>
public sealed class Cog
{
    public int Num;
    public string Name = string.Empty;
    public List<string> Values { get; } = new();
}

/// <summary>A thing template: a named bundle of parameters with optional parent inheritance.</summary>
public sealed class Template
{
    public string Name = string.Empty;
    public string Parent = string.Empty;

    /// <summary>
    /// Declaration order in the TEMPLATES section. Kept explicitly because
    /// deleting from a Dictionary can perturb enumeration order, which would
    /// otherwise churn the whole section on the next save.
    /// </summary>
    public int Order;
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Ordered imported parameters, retaining repeated engine entries such as frame=.</summary>
    public List<KeyValuePair<string, string>> SourceParameters { get; } = new();
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Lighting;

/// <summary>
/// An explicit editor sampling profile, not a recovered retail bake algorithm.
/// Samples are non-directional, unoccluded source-point contributions: no
/// normals, ambient floor, world/model shadows, or legacy RGB-range semantics.
/// Preparation and status queries never modify the level or model inputs.
/// </summary>
public static class ArchitecturalLightingAuthoring
{
    public const string BakeProfile = "source-points-v1";
    /// <summary>Maximum vertex/light pairs evaluated by one source-point preparation.</summary>
    public const long MaxSampleLightPairs = 50_000_000;

    public static ArchitecturalLightObject PrepareSamples(Level level,
        IReadOnlyList<IReadOnlyList<Vec3>> worldMeshes)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(worldMeshes);
        RequireMotS(level);
        ValidateLights(level);
        long vertices = 0;
        foreach (var mesh in worldMeshes)
        {
            if (mesh is null) throw new ArgumentException("A sample mesh cannot be null.", nameof(worldMeshes));
            if (mesh.Count < 0 || vertices > long.MaxValue - mesh.Count)
                throw new InvalidOperationException("Architectural sample vertex count exceeds the supported range.");
            vertices += mesh.Count;
        }
        // Divide before comparing so even hostile collection counts cannot
        // overflow the work estimate. No output arrays have been allocated.
        if (level.Lights.Count > 0 && vertices > MaxSampleLightPairs / level.Lights.Count)
            throw new InvalidOperationException($"Architectural source-point bake exceeds the {MaxSampleLightPairs} vertex/light-pair budget.");
        foreach (var mesh in worldMeshes)
        {
            foreach (var point in mesh) RequireFinite(point, "Model sample position");
        }

        var result = new ArchitecturalLightObject();
        foreach (var points in worldMeshes)
        {
            var mesh = new ArchitecturalLightMesh();
            foreach (var point in points)
            {
                double mono = 0, red = 0, green = 0, blue = 0;
                foreach (var light in level.Lights)
                {
                    if (light.Range == 0) continue;
                    // Normalize by range first. This avoids squared-distance
                    // overflow for finite distant coordinates and small ranges.
                    double dx = (light.Position.X - point.X) / light.Range;
                    double dy = (light.Position.Y - point.Y) / light.Range;
                    double dz = (light.Position.Z - point.Z) / light.Range;
                    if (System.Math.Abs(dx) >= 1 || System.Math.Abs(dy) >= 1 || System.Math.Abs(dz) >= 1) continue;
                    double distance = System.Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (distance >= 1) continue;
                    double remaining = 1 - distance;
                    double falloff = remaining * remaining;
                    mono += light.Intensity * falloff;
                    double rgb = light.EffectiveRgbIntensity * falloff;
                    red += light.Color.R * rgb;
                    green += light.Color.G * rgb;
                    blue += light.Color.B * rgb;
                }
                var sample = new ArchitecturalLightVertex(mono, red, green, blue);
                ValidateGeneratedSample(sample);
                mesh.Vertices.Add(sample);
            }
            result.Meshes.Add(mesh);
        }
        return result;
    }

    /// <summary>
    /// Digests the exact pose and resolved sampling inputs. Resource bytes have
    /// their own provenance hash. Geometry, sector, flags, ambient and nullable
    /// RGB representation are excluded when they do not alter this profile.
    /// </summary>
    public static string InputsDigest(Level level, Thing thing)
    {
        RequireLiveThing(level, thing);
        RequireMotS(level);
        RequireFinite(thing.Position, "Thing position");
        RequireFinite(thing.Pitch, "Thing pitch"); RequireFinite(thing.Yaw, "Thing yaw"); RequireFinite(thing.Roll, "Thing roll");
        ValidateLights(level);
        string model = level.GetThingModel(thing);
        bool eligible = IsLevelGeometry(level, thing);
        return Digest(writer =>
        {
            writer.Write("SED-ARCH-INPUTS-1"); writer.Write(BakeProfile);
            writer.Write((int)level.Kind); writer.Write(model.ToLowerInvariant()); writer.Write(eligible);
            WritePoint(writer, thing.Position);
            writer.Write(thing.Pitch); writer.Write(thing.Yaw); writer.Write(thing.Roll);
            writer.Write(level.Lights.Count);
            foreach (var light in level.Lights)
            {
                WritePoint(writer, light.Position);
                writer.Write(light.Range); writer.Write(light.Intensity); writer.Write(light.EffectiveRgbIntensity);
                writer.Write(light.Color.R); writer.Write(light.Color.G); writer.Write(light.Color.B);
            }
        });
    }

    public static string SamplesDigest(ArchitecturalLightObject samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return Digest(writer =>
        {
            writer.Write("SED-ARCH-SAMPLES-1"); writer.Write(samples.Meshes.Count);
            foreach (var mesh in samples.Meshes)
            {
                if (mesh is null) throw new InvalidOperationException("Architectural lighting contains a null mesh.");
                writer.Write(mesh.Vertices.Count);
                foreach (var sample in mesh.Vertices)
                {
                    // Historical arrays may contain finite negative values.
                    // Digest retained bytes without imposing new-bake policy.
                    RequireFinite(sample.Mono, "Architectural mono sample"); RequireFinite(sample.Red, "Architectural red sample");
                    RequireFinite(sample.Green, "Architectural green sample"); RequireFinite(sample.Blue, "Architectural blue sample");
                    writer.Write(sample.Mono); writer.Write(sample.Red); writer.Write(sample.Green); writer.Write(sample.Blue);
                }
            }
        });
    }

    /// <summary>
    /// Reports local input/sample consistency only. Even current-inputs does
    /// not assert that the model resource still matches ModelSha256.
    /// </summary>
    public static string Status(Level level, Thing thing)
    {
        RequireLiveThing(level, thing);
        if (thing.ArchLightingIndex == -1) return "unbound";
        if ((uint)thing.ArchLightingIndex >= level.ArchitecturalLighting.Count || thing.ArchLightingProvenance is not { } provenance)
            return "unknown";
        if (!ValidProvenance(provenance)) return "unknown";
        try
        {
            if (!SamplesDigest(level.ArchitecturalLighting[thing.ArchLightingIndex]).Equals(provenance.SamplesSha256, StringComparison.OrdinalIgnoreCase))
                return "overridden";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return "overridden"; }
        try
        {
            if (level.Kind != ProjectType.MysteriesOfTheSith || !IsLevelGeometry(level, thing)
                || !level.GetThingModel(thing).Equals(provenance.ModelName, StringComparison.OrdinalIgnoreCase)) return "stale";
            if (provenance.Origin != BakeProfile) return provenance.Origin;
            return InputsDigest(level, thing).Equals(provenance.InputSha256, StringComparison.OrdinalIgnoreCase)
                ? "current-inputs" : "stale";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException) { return "stale"; }
    }

    internal static void RequireLiveThing(Level level, Thing thing)
    {
        ArgumentNullException.ThrowIfNull(level); ArgumentNullException.ThrowIfNull(thing);
        if (thing.Level != level || level.Things.Count(candidate => ReferenceEquals(candidate, thing)) != 1)
            throw new InvalidOperationException("Architectural lighting requires a distinct live thing in its owning level.");
    }

    internal static void RequireEligibleModel(Level level, Thing thing, string model)
    {
        RequireLiveThing(level, thing); RequireMotS(level);
        if (string.IsNullOrWhiteSpace(model) || model.Equals("none", StringComparison.OrdinalIgnoreCase)
            || !model.Equals(level.GetThingModel(thing), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Architectural lighting requires the thing's current resolved model name.");
        if (!IsLevelGeometry(level, thing))
            throw new InvalidOperationException("Architectural lighting requires resolved thingflags level-geometry bit 0x8.");
    }

    internal static bool ValidProvenance(ArchitectureLightingProvenance provenance) =>
        provenance.Origin is "bound" or "reset" or BakeProfile
        && !string.IsNullOrWhiteSpace(provenance.ModelName)
        && IsHash(provenance.ModelSha256) && IsHash(provenance.SamplesSha256)
        && (provenance.Origin == BakeProfile ? IsHash(provenance.InputSha256) : provenance.InputSha256 == "");

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool IsLevelGeometry(Level level, Thing thing)
    {
        string value = thing.Values.TryGetValue("thingflags", out var direct) ? direct : level.GetTemplateValue(thing.Template, "thingflags");
        if (string.IsNullOrEmpty(value)) return false;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint flags))
            throw new FormatException("Thing has invalid hexadecimal thingflags.");
        return (flags & 0x8) != 0;
    }
    private static void RequireMotS(Level level)
    {
        if (level.Kind != ProjectType.MysteriesOfTheSith)
            throw new InvalidOperationException("Architectural lighting authoring supports MotS only.");
    }
    private static void ValidateLights(Level level)
    {
        foreach (var light in level.Lights)
        {
            if (light is null) throw new InvalidOperationException("Source lights contain a null entry.");
            RequireFinite(light.Position, "Source light position");
            Nonnegative(light.Range, "Source light range"); Nonnegative(light.Intensity, "Source white intensity");
            Nonnegative(light.EffectiveRgbIntensity, "Source effective RGB intensity");
            Nonnegative(light.Color.R, "Source red color"); Nonnegative(light.Color.G, "Source green color");
            Nonnegative(light.Color.B, "Source blue color");
        }
    }
    private static void ValidateGeneratedSample(ArchitecturalLightVertex sample)
    {
        Nonnegative(sample.Mono, "Architectural mono sample"); Nonnegative(sample.Red, "Architectural red sample");
        Nonnegative(sample.Green, "Architectural green sample"); Nonnegative(sample.Blue, "Architectural blue sample");
    }
    private static void Nonnegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0) throw new InvalidOperationException($"{name} must be finite and nonnegative.");
    }
    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException($"{name} must be finite.");
    }
    private static void RequireFinite(Vec3 value, string name)
    {
        RequireFinite(value.X, name); RequireFinite(value.Y, name); RequireFinite(value.Z, name);
    }
    private static void WritePoint(BinaryWriter writer, Vec3 point)
    { writer.Write(point.X); writer.Write(point.Y); writer.Write(point.Z); }
    private static string Digest(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)) write(writer);
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)))).ToLowerInvariant();
    }
}

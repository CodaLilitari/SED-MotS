namespace Sed.Core.Lighting;

public readonly record struct VertexColorSample(float White, float Red, float Green, float Blue);
public enum VertexRgbStorage { Retained, Compacted, Scalar, Unknown, Invalid }
public sealed record VertexColorReadiness(VertexRgbStorage Retail, VertexRgbStorage OpenMots,
    int? DistinguishingCorner, string Reason);

/// <summary>Static storage inference for the inspected loaders, not a runtime or visibility test.</summary>
public static class DynamicVertexColorReadiness
{
    public static VertexColorReadiness Evaluate(IReadOnlyList<VertexColorSample> samples, int componentsPerCorner)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count is < 3 or > 24 || componentsPerCorner is not (1 or 4))
            return new(VertexRgbStorage.Invalid, VertexRgbStorage.Invalid, null, "Invalid corner count or lighting row layout.");
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (!float.IsFinite(s.White) || !float.IsFinite(s.Red) || !float.IsFinite(s.Green) || !float.IsFinite(s.Blue))
                return new(VertexRgbStorage.Invalid, VertexRgbStorage.Invalid, null, "Non-finite float32 lighting is outside this inference.");
        }
        if (componentsPerCorner == 1)
            return new(VertexRgbStorage.Unknown, VertexRgbStorage.Scalar, null,
                "Serialized scalar row: current OpenMotS clears the RGB flag and its RGB setter is unavailable; retail scalar-row behavior is unverified.");
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (s.White != s.Red || s.White != s.Green)
                return new(VertexRgbStorage.Retained, VertexRgbStorage.Retained, i,
                    "At least one final float32 W/R/G difference preserves RGB storage for the entire surface in the inspected loaders.");
        }
        return new(VertexRgbStorage.Compacted, VertexRgbStorage.Retained, null,
            "Every final float32 corner has W == R == G. Inspected retail compacts the surface and its RGB setter cannot write; blue does not affect that decision. Current OpenMotS retains the 4N row.");
    }
}

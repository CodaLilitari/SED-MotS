using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Tests;

/// <summary>Original analytic counterexamples, not a general numerical-robustness claim.</summary>
public sealed class LightingPredicatePrecisionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThinPlanarTriangleContainsItsInteriorAndBlocksTheSharedRay(bool reverse)
    {
        var (level, face) = ThinTriangle(reverse);
        Assert.Equal(1, face.CalculateNormal().LengthSquared);
        Assert.True(GeometryOps.PointOnSurface(face, new(.5, 0, 0), face.CalculateNormal()));
        Assert.False(GeometryOps.PointOnSurface(face, new(1.75, 0, 0), face.CalculateNormal()));
        var blocked = LightCalculator.CreateWorldOcclusion(level);
        Assert.True(blocked(new(.5, 0, -1), new(.5, 0, 1)));
        Assert.False(blocked(new(1.75, 0, -1), new(1.75, 0, 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedThinTriangleShadowsBothArchChannelsWithoutChangingOriginalProfile(bool reverse)
    {
        var (level, _) = ThinTriangle(reverse);
        level.Lights.Add(new Light
        {
            Position = new(.5, 0, -1), Range = 4, Intensity = .8,
            RgbIntensity = .4, Color = new(1, .5f, .25f),
        });
        IReadOnlyList<IReadOnlyList<Vec3>> meshes = new IReadOnlyList<Vec3>[] { new[] { new Vec3(.5, 0, 1) } };
        var original = ArchitecturalLightingAuthoring.PrepareSamples(level, meshes);
        var occluded = ArchitecturalLightingAuthoring.PrepareSamples(level, meshes,
            ArchitecturalLightingAuthoring.WorldOccludedBakeProfile);
        Assert.Equal(new ArchitecturalLightVertex(.2, .1, .05, .025), original.Meshes[0].Vertices[0]);
        Assert.Equal(default, occluded.Meshes[0].Vertices[0]);
        Assert.Empty(level.ArchitecturalLighting);
    }

    private static (Level Level, Surface Face) ThinTriangle(bool reverse)
    {
        var level = new Level { Kind = ProjectType.MysteriesOfTheSith };
        var sector = level.NewSector(); var face = sector.NewSurface();
        Vec3[] points = [new(-1, -1e-31, 0), new(1, 1e-31, 0), new(2, -1e-31, 0)];
        if (reverse) Array.Reverse(points);
        foreach (var point in points) face.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(point) });
        return (level, face);
    }
}

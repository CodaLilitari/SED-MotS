using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Tests;

public sealed class AlignTextureEdgeIdentityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExactShortEdgeKeepsDistinctEndpointUvsAndUndo(bool reverseReference, bool reverseTarget)
    {
        const double height = 5e-7; // Both endpoints are inside the old 1e-6 UvAt tolerance.
        var reference = Quad(-1, 0, height);
        var target = Quad(0, 1, height);
        Map(reference, p => new TexVertex((p.X + 1) * 64, p.Y / height * 64));
        if (reverseReference) reference.Corners.Reverse();
        if (reverseTarget) target.Corners.Reverse();
        var old = target.Corners.Select(c => c.Uv).ToArray();
        var referenceUvs = reference.Corners.Select(c => c.Uv).ToArray();
        var history = new EditHistory();

        Assert.Null(AlignTextureToNeighbourCommand.Validate(target, reference));
        history.Do(new AlignTextureToNeighbourCommand(target, reference));
        foreach (var c in target.Corners)
        {
            Assert.Equal((c.Vertex.Position.X + 1) * 64, c.Uv.U, 10);
            Assert.Equal(c.Vertex.Position.Y / height * 64, c.Uv.V, 10);
        }
        Assert.Equal(referenceUvs, reference.Corners.Select(c => c.Uv));
        var aligned = target.Corners.Select(c => c.Uv).ToArray();
        history.Undo(); Assert.Equal(old, target.Corners.Select(c => c.Uv));
        history.Redo(); Assert.Equal(aligned, target.Corners.Select(c => c.Uv));
    }

    [Fact]
    public void UniqueNearOnlyEdgeUsesItsMatchedReferenceBasis()
    {
        var reference = Quad(-1, 0, 1);
        var target = Quad(0, 1, 1);
        foreach (var c in target.Corners) c.Vertex.Position += new Vec3(2e-7, 3e-7, 0);
        Map(reference, p => new TexVertex((p.X + 1) * 64 + p.Y * 16, p.Y * 32));
        Assert.Null(AlignTextureToNeighbourCommand.Validate(target, reference));
        new AlignTextureToNeighbourCommand(target, reference).Apply();
        foreach (var c in target.Corners)
        {
            double x = c.Vertex.Position.X - 2e-7, y = c.Vertex.Position.Y - 3e-7;
            Assert.Equal((x + 1) * 64 + y * 16, c.Uv.U, 9);
            Assert.Equal(y * 32, c.Uv.V, 9);
        }
    }

    [Fact]
    public void NearOnlyShortEdgeWithTwoPossibleOrientationsRefusesAtomically()
    {
        var reference = Quad(-1, 0, 5e-7);
        var target = Quad(0, 1, 5e-7);
        foreach (var c in target.Corners) c.Vertex.Position += new Vec3(0, 1e-7, 0);
        Map(reference, p => new TexVertex((p.X + 1) * 64, p.Y / 5e-7 * 64));
        RefusesWithoutMutation(target, reference, "ambiguous endpoint orientation");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 4)]
    [InlineData(true, 0)]
    [InlineData(true, 4)]
    public void MultipleSharedEdgesRefuseRegardlessOfCornerOrder(bool warped, int rotation)
    {
        var reference = Face(new(-1, 0, 0), new(0, 0, 0), new(0, .5, 0), new(0, 1, 0), new(-1, 1, 0));
        var target = Face(new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, .5, 0));
        Map(reference, p => new TexVertex((p.X + 1) * 64, p.Y * 64));
        if (warped) reference.Corners[2].Uv = new TexVertex(64, 1000);
        var corners = target.Corners.ToArray();
        target.Corners.Clear();
        target.Corners.AddRange(corners.Skip(rotation).Concat(corners.Take(rotation)));
        RefusesWithoutMutation(target, reference, "multiple shared edges");
    }

    [Fact]
    public void NonAffineSingleEdgeReferenceRefusesInsteadOfDiscardingItsOtherGradient()
    {
        var reference = Quad(-1, 0, 1); var target = Quad(0, 1, 1);
        Map(reference, p => new TexVertex((p.X + 1) * 64, p.Y * 64));
        reference.Corners[3].Uv = new TexVertex(0, 128);
        RefusesWithoutMutation(target, reference, "affine reference UV mapping");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AffineRotatedAndShearedMappingContinuesForEitherWinding(bool reverse)
    {
        var reference = Quad(-1, 0, 1); var target = Quad(0, 1, 1);
        static TexVertex Mapping(Vec3 p) => new(7 + 13 * p.X + 17 * p.Y, -5 + 19 * p.X - 23 * p.Y);
        Map(reference, Mapping);
        if (reverse) { reference.Corners.Reverse(); target.Corners.Reverse(); }
        Assert.Null(AlignTextureToNeighbourCommand.Validate(target, reference));
        new AlignTextureToNeighbourCommand(target, reference).Apply();
        foreach (var c in target.Corners) Assert.Equal(Mapping(c.Vertex.Position), c.Uv);
    }

    [Fact]
    public void AffineMappingContinuesAcrossRightAngleFold()
    {
        var reference = Quad(-1, 0, 1);
        var target = Face(new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0));
        Map(reference, p => new TexVertex((p.X + 1) * 64 + p.Y * 16, p.Y * 32));
        Assert.Null(AlignTextureToNeighbourCommand.Validate(target, reference));
        new AlignTextureToNeighbourCommand(target, reference).Apply();
        foreach (var c in target.Corners)
        {
            Assert.Equal((c.Vertex.Position.Z + 1) * 64 + c.Vertex.Position.Y * 16, c.Uv.U, 10);
            Assert.Equal(c.Vertex.Position.Y * 32, c.Uv.V, 10);
        }
    }

    private static void RefusesWithoutMutation(Surface target, Surface reference, string reason)
    {
        var original = target.Corners.Select(c => c.Uv).ToArray();
        var referenceOriginal = reference.Corners.Select(c => c.Uv).ToArray();
        var targetNormal = target.Normal; var referenceNormal = reference.Normal;
        Assert.Contains(reason, AlignTextureToNeighbourCommand.Validate(target, reference));
        var history = new EditHistory();
        var error = Assert.Throws<InvalidOperationException>(() => history.Do(new AlignTextureToNeighbourCommand(target, reference)));
        Assert.Contains(reason, error.Message); Assert.False(history.CanUndo);
        Assert.Equal(original, target.Corners.Select(c => c.Uv));
        Assert.Equal(referenceOriginal, reference.Corners.Select(c => c.Uv));
        Assert.Equal(targetNormal, target.Normal); Assert.Equal(referenceNormal, reference.Normal);
    }

    private static void Map(Surface surface, Func<Vec3, TexVertex> map)
    {
        foreach (var c in surface.Corners) c.Uv = map(c.Vertex.Position);
    }

    private static Surface Quad(double left, double right, double height) =>
        Face(new(left, 0, 0), new(right, 0, 0), new(right, height, 0), new(left, height, 0));

    private static Surface Face(params Vec3[] points)
    {
        var sector = new Level().NewSector(); var surface = sector.NewSurface();
        foreach (var point in points)
            surface.Corners.Add(new Surface.Corner { Vertex = sector.AddVertex(point), Uv = new TexVertex(-12, 37) });
        surface.RecalcNormal(); return surface;
    }
}

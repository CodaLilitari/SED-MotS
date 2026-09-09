using System.Runtime.InteropServices;
using Sed.Core.Math;
using Sed.Rendering;

namespace Sed.Core.Tests;

public sealed class RendererLightingLayoutTests
{
    [Fact]
    public void VertexLayoutCarriesIndependentMonoWithoutMovingExistingAttributes()
    {
        Assert.Equal(48, Marshal.SizeOf<MeshVertex>());
        Assert.Equal(48u, MeshVertex.Stride);
        Assert.Equal(0, Marshal.OffsetOf<MeshVertex>(nameof(MeshVertex.Px)).ToInt32());
        Assert.Equal(12, Marshal.OffsetOf<MeshVertex>(nameof(MeshVertex.Nx)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<MeshVertex>(nameof(MeshVertex.R)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<MeshVertex>(nameof(MeshVertex.U)).ToInt32());
        Assert.Equal(44, Marshal.OffsetOf<MeshVertex>(nameof(MeshVertex.Mono)).ToInt32());
        Assert.Equal(44u, MeshVertex.MonoOffset);
        var vertex = new MeshVertex(new Vec3(1, 2, 3), Vec3.Zero, new ColorF(.1f, .3f, .9f), 4, 5, .7f);
        var bytes = MemoryMarshal.AsBytes<MeshVertex>(new[] { vertex }.AsSpan());
        Assert.Equal(.1f, BitConverter.ToSingle(bytes.Slice(24, 4)));
        Assert.Equal(.3f, BitConverter.ToSingle(bytes.Slice(28, 4)));
        Assert.Equal(.9f, BitConverter.ToSingle(bytes.Slice(32, 4)));
        Assert.Equal(.7f, BitConverter.ToSingle(bytes.Slice(44, 4)));
    }

    [Fact]
    public void ExistingVertexConstructorDefaultsMonoToRedAndSceneToMono()
    {
        var vertex = new MeshVertex(Vec3.Zero, Vec3.Zero, new ColorF(.25f, .5f, .75f));
        Assert.Equal(.25f, vertex.Mono);
        Assert.Equal(RenderLightingMode.Mono, new RenderScene().LightingMode);
        Assert.True(new RenderScene().UsePaletteLightRamp);
    }

    [Theory]
    [InlineData(767, 16384)]
    [InlineData(769, 16384)]
    [InlineData(768, 16383)]
    [InlineData(768, 16385)]
    public void ColormapRejectsTruncatedAndOversizedTables(int paletteBytes, int rampBytes) =>
        Assert.Throws<ArgumentException>(() => new ColormapData(new byte[paletteBytes], new byte[rampBytes]).Validate());

    [Fact]
    public void ColormapRejectsDefaultAndAcceptsCompleteTables()
    {
        Assert.Throws<ArgumentException>(() => default(ColormapData).Validate());
        new ColormapData(new byte[768], new byte[16384]).Validate();
    }

    [Fact]
    public void PackedLightingStatePreservesPushSizeAndSeparatesSolidAndFaceMode()
    {
        Assert.Equal(156, RenderLightingState.PushOffset);
        Assert.Equal(160, RenderLightingState.PushBytes);
        Assert.Equal(0x300u, RenderLightingState.Pack(RenderLightingMode.Mono, false, 3));
        Assert.Equal(0x301u, RenderLightingState.Pack(RenderLightingMode.Rgb, false, 3));
        Assert.Equal(0x303u, RenderLightingState.Pack(RenderLightingMode.Rgb, true, 3));
        Assert.Equal(0x203u, RenderLightingState.Pack(RenderLightingMode.Rgb, true, 2));
        Assert.Equal(0x103u, RenderLightingState.Pack(RenderLightingMode.Rgb, true, 1));
        Assert.Equal(0x003u, RenderLightingState.Pack(RenderLightingMode.Rgb, true, 0));
        Assert.Equal(3, new Submesh().LightMode);
        Assert.False(new IndexedTexture(1, 1, new byte[] { 1 }).IsSolid);
    }

    [Fact]
    public void ScalarProfileBitDoesNotChangeMaterialOrChannelBits()
    {
        Assert.Equal(4u, RenderLightingState.ScalarModulationBit);
        foreach (var lighting in new[] { RenderLightingMode.Mono, RenderLightingMode.Rgb })
            foreach (bool solid in new[] { false, true })
                foreach (int lightMode in new[] { 0, 1, 2, 3 })
                {
                    uint comparison = RenderLightingState.Pack(lighting, solid, lightMode);
                    uint engine = RenderLightingState.Pack(lighting, solid, lightMode, usePaletteLightRamp: false);
                    Assert.Equal(comparison | 4u, engine);
                    Assert.Equal((uint)lightMode, engine >> RenderLightingState.LightModeShift);
                }
        Assert.False(new Submesh().UseMonochromeLighting);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void PackedLightingStateRejectsModeBitsThatWouldSpill(int mode) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderLightingState.Pack(RenderLightingMode.Mono, false, mode));
}

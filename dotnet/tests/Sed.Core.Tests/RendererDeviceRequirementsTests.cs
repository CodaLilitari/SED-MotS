using Sed.Rendering.Vulkan;

namespace Sed.Core.Tests;

public sealed class RendererDeviceRequirementsTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(128u)]
    [InlineData(159u)]
    public void DeviceBelowShaderLimitIsRefusedWithActualAndRequiredCapacity(uint maximum)
    {
        var error = Assert.Throws<VulkanException>(() =>
            RendererDeviceRequirements.RequirePushConstants("Synthetic Vulkan device", maximum));

        Assert.False(RendererDeviceRequirements.SupportsPushConstants(maximum));
        Assert.Contains("Synthetic Vulkan device", error.Message);
        Assert.Contains($"supports {maximum} bytes", error.Message);
        Assert.Contains("requires 160 bytes", error.Message);
    }

    [Theory]
    [InlineData(160u)]
    [InlineData(256u)]
    [InlineData(uint.MaxValue)]
    public void DeviceAtOrAboveShaderLimitIsAccepted(uint maximum)
    {
        RendererDeviceRequirements.RequirePushConstants("Synthetic Vulkan device", maximum);
        Assert.True(RendererDeviceRequirements.SupportsPushConstants(maximum));
    }
}

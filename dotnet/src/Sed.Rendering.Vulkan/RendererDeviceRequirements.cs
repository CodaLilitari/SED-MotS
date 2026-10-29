namespace Sed.Rendering.Vulkan;

/// <summary>Checks device limits required by the existing scene shader layout.</summary>
public static class RendererDeviceRequirements
{
    public static bool SupportsPushConstants(uint maxPushConstantsSize)
        => maxPushConstantsSize >= RenderLightingState.PushBytes;

    public static void RequirePushConstants(string deviceName, uint maxPushConstantsSize)
    {
        if (!SupportsPushConstants(maxPushConstantsSize))
            throw new VulkanException($"Vulkan device '{deviceName}' supports {maxPushConstantsSize} bytes of push constants; " +
                $"the scene renderer requires {RenderLightingState.PushBytes} bytes. Select a compatible Vulkan device or driver.");
    }
}

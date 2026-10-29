using Sed.Rendering;
using Sed.Rendering.Vulkan;

// Smoke test: prove the Vulkan/MoltenVK path is wired up on this machine before
// we build the real renderer on top of it. Creates an instance and lists GPUs.
Console.WriteLine($"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"Arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Loader: {NativeVulkanLocator.ResolveLoaderPath()}");

try
{
    using var ctx = VulkanContext.Create("SED Vulkan Smoke");
    Console.WriteLine("vkCreateInstance: OK");

    var gpus = ctx.EnumerateGpus();
    if (gpus.Count == 0)
    {
        Console.WriteLine("No Vulkan physical devices found.");
        return 2;
    }

    Console.WriteLine($"Found {gpus.Count} GPU(s):");
    foreach (var g in gpus)
    {
        Console.WriteLine($"  - {g.Name} [{g.Type}] Vulkan {g.ApiVersionString}");
        string support = RendererDeviceRequirements.SupportsPushConstants(g.MaxPushConstantsSize) ? "supported" : "unsupported";
        Console.WriteLine($"    maxPushConstantsSize: {g.MaxPushConstantsSize} bytes; scene renderer push constants: {support} (requires {RenderLightingState.PushBytes} bytes)");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.Message}");
    return 1;
}

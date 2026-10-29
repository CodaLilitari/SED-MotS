using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Sed.Rendering.Vulkan;

/// <summary>Info about a Vulkan-capable GPU discovered at startup.</summary>
public readonly record struct GpuInfo(
    string Name,
    PhysicalDeviceType Type,
    uint ApiVersion,
    uint DriverVersion,
    uint MaxPushConstantsSize)
{
    // Vulkan packs the version as (major<<22)|(minor<<12)|patch.
    public string ApiVersionString =>
        $"{ApiVersion >> 22}.{(ApiVersion >> 12) & 0x3FF}.{ApiVersion & 0xFFF}";
}

/// <summary>
/// Owns the Vulkan instance and enumerates physical devices. On macOS the
/// instance is created with the portability-enumeration flag/extension so the
/// MoltenVK ICD (a non-conformant "portability" driver) is reported.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    private const string PortabilityEnumerationExtension = "VK_KHR_portability_enumeration";

    public Vk Vk { get; }
    public Instance Instance { get; private set; }

    private VulkanContext(Vk vk, Instance instance)
    {
        Vk = vk;
        Instance = instance;
    }

    /// <summary>Creates a Vulkan instance, configuring MoltenVK on macOS.</summary>
    public static VulkanContext Create(string appName = "SED")
    {
        NativeVulkanLocator.Configure();

        // Load the loader by absolute path: on macOS, dlopen() does not observe a
        // DYLD_LIBRARY_PATH that was changed after process launch.
        var loaderPath = NativeVulkanLocator.ResolveLoaderPath();
        var nativeContext = new DefaultNativeContext(loaderPath);
        var vk = new Vk(nativeContext);

        bool portability = OperatingSystem.IsMacOS();

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr(appName),
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("SedRenderer"),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version12,
        };

        var extensions = new List<string>();
        if (portability)
            extensions.Add(PortabilityEnumerationExtension);

        var ppExtensions = (byte**)SilkMarshal.StringArrayToPtr(extensions);

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = ppExtensions,
            Flags = portability
                ? InstanceCreateFlags.EnumeratePortabilityBitKhr
                : InstanceCreateFlags.None,
        };

        try
        {
            var result = vk.CreateInstance(in createInfo, null, out var instance);
            if (result != Result.Success)
                throw new VulkanException($"vkCreateInstance failed: {result}");
            return new VulkanContext(vk, instance);
        }
        finally
        {
            SilkMarshal.Free((nint)appInfo.PApplicationName);
            SilkMarshal.Free((nint)appInfo.PEngineName);
            SilkMarshal.Free((nint)ppExtensions);
        }
    }

    /// <summary>Enumerates all physical devices reported by the instance.</summary>
    public IReadOnlyList<GpuInfo> EnumerateGpus()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0)
            return Array.Empty<GpuInfo>();

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pDevices = devices)
            Vk.EnumeratePhysicalDevices(Instance, ref count, pDevices);

        var gpus = new List<GpuInfo>((int)count);
        foreach (var device in devices)
        {
            Vk.GetPhysicalDeviceProperties(device, out var props);
            var name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "<unknown>";
            gpus.Add(new GpuInfo(name, props.DeviceType, props.ApiVersion, props.DriverVersion, props.Limits.MaxPushConstantsSize));
        }
        return gpus;
    }

    public void Dispose()
    {
        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }
        Vk.Dispose();
    }
}

public sealed class VulkanException(string message) : Exception(message);

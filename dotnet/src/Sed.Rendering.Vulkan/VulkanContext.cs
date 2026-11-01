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
/// Owns the Vulkan instance and enumerates physical devices. On macOS an
/// advertised portability-enumeration extension enables portability drivers;
/// a directly loaded driver need not expose that loader extension.
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

        InstanceExtensionSelection extensions;
        try
        {
            bool isMacOS = OperatingSystem.IsMacOS();
            extensions = SelectInstanceExtensions(isMacOS, isMacOS ? EnumerateInstanceExtensions(vk) : []);
        }
        catch
        {
            vk.Dispose();
            throw;
        }

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr(appName),
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("SedRenderer"),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version12,
        };

        var ppExtensions = (byte**)SilkMarshal.StringArrayToPtr(extensions.Names);

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Names.Length,
            PpEnabledExtensionNames = ppExtensions,
            Flags = extensions.Flags,
        };

        try
        {
            var result = vk.CreateInstance(in createInfo, null, out var instance);
            if (result != Result.Success)
                throw new VulkanException($"vkCreateInstance failed: {result}");
            return new VulkanContext(vk, instance);
        }
        catch
        {
            vk.Dispose();
            throw;
        }
        finally
        {
            SilkMarshal.Free((nint)appInfo.PApplicationName);
            SilkMarshal.Free((nint)appInfo.PEngineName);
            SilkMarshal.Free((nint)ppExtensions);
        }
    }

    internal static InstanceExtensionSelection SelectInstanceExtensions(bool isMacOS, IEnumerable<string> advertisedExtensions) =>
        isMacOS && advertisedExtensions.Contains(PortabilityEnumerationExtension, StringComparer.Ordinal)
            ? new([PortabilityEnumerationExtension], InstanceCreateFlags.EnumeratePortabilityBitKhr)
            : new([], InstanceCreateFlags.None);

    private static IReadOnlyList<string> EnumerateInstanceExtensions(Vk vk)
    {
        // The advertised list can grow between the count and data calls. Retry
        // incomplete enumerations, but do not loop indefinitely on a broken driver.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint count = 0;
            var result = vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, null);
            if (result != Result.Success)
                throw new VulkanException($"vkEnumerateInstanceExtensionProperties failed: {result}");
            if (count == 0)
                return Array.Empty<string>();

            var properties = new ExtensionProperties[count];
            fixed (ExtensionProperties* pProperties = properties)
                result = vk.EnumerateInstanceExtensionProperties((byte*)null, ref count, pProperties);
            if (result == Result.Incomplete)
                continue;
            if (result != Result.Success)
                throw new VulkanException($"vkEnumerateInstanceExtensionProperties failed: {result}");

            var names = new List<string>((int)count);
            for (int i = 0; i < count; i++)
            {
                fixed (byte* pName = properties[i].ExtensionName)
                {
                    var name = SilkMarshal.PtrToString((nint)pName);
                    if (name is not null)
                        names.Add(name);
                }
            }
            return names;
        }
        throw new VulkanException("vkEnumerateInstanceExtensionProperties remained incomplete after four attempts");
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

internal readonly record struct InstanceExtensionSelection(string[] Names, InstanceCreateFlags Flags);

public sealed class VulkanException(string message) : Exception(message);

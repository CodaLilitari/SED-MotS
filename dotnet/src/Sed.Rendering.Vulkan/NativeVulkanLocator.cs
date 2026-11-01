using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Sed.Core.Tests")]

namespace Sed.Rendering.Vulkan;

/// <summary>
/// Resolves the native Vulkan loader and (on macOS) the MoltenVK ICD at runtime.
/// Mac distributions carry MoltenVK beside the apphost. Development installs can
/// use the current process architecture's Homebrew loader and MoltenVK ICD instead.
/// Explicit Vulkan driver overrides always retain generic loader dispatch.
/// </summary>
public static class NativeVulkanLocator
{
    /// <summary>Ensures the loader is discoverable and the MoltenVK ICD is configured. Idempotent.</summary>
    public static void Configure()
    {
        if (!OperatingSystem.IsMacOS())
            return; // Linux/Windows use the system loader on the default path.

        var configuration = ResolveMacConfiguration();
        // Direct MoltenVK does not consult ICD manifests. Setting one here would
        // also make the next resolution mistake our own setting for an override.
        if (!configuration.DirectMoltenVk && configuration.IcdPath is not null)
            Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", configuration.IcdPath);
        // VulkanContext loads the selected absolute path; changing DYLD_LIBRARY_PATH
        // after process startup does not make that library discoverable to dlopen.
    }

    /// <summary>Uses the native loader name for each platform; macOS also searches its bundle and Homebrew.</summary>
    public static string ResolveLoaderPath()
    {
        if (OperatingSystem.IsWindows())
        {
            string systemLoader = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
            return File.Exists(systemLoader) ? systemLoader : "vulkan-1.dll";
        }
        if (OperatingSystem.IsLinux()) return "libvulkan.so.1";
        return OperatingSystem.IsMacOS() ? ResolveMacConfiguration().LoaderPath : "vulkan";
    }

    private static MacVulkanResolution ResolveMacConfiguration() => ResolveMacConfiguration(
        AppContext.BaseDirectory, RuntimeInformation.ProcessArchitecture,
        Environment.GetEnvironmentVariable("VK_DRIVER_FILES"), Environment.GetEnvironmentVariable("VK_ICD_FILENAMES"),
        File.Exists, path => Directory.Exists(path) ? Directory.EnumerateDirectories(path) : []);

    /// <summary>Chooses paths without loading libraries or changing process environment.</summary>
    internal static MacVulkanResolution ResolveMacConfiguration(string baseDirectory, Architecture processArchitecture,
        string? driverFiles, string? icdFilenames, Func<string, bool> fileExists,
        Func<string, IEnumerable<string>> enumerateDirectories)
    {
        const string genericLoader = "libvulkan.1.dylib";
        string? prefix = processArchitecture switch
        {
            Architecture.Arm64 => "/opt/homebrew",
            Architecture.X64 => "/usr/local",
            _ => null,
        };
        if (prefix is null)
            return new(genericLoader, null, false);

        bool hasOverride = !string.IsNullOrEmpty(driverFiles) || !string.IsNullOrEmpty(icdFilenames);
        string bundled = Path.Combine(baseDirectory, "libMoltenVK.dylib");
        if (!hasOverride && fileExists(bundled))
            return new(bundled, null, true);

        string loaderRelative = Path.Combine("lib", "libvulkan.dylib");
        string? loader = BrewCandidates(prefix, "vulkan-loader", loaderRelative,
            Path.Combine(prefix, "lib", "libvulkan.dylib"), enumerateDirectories).FirstOrDefault(fileExists);
        string? icd = null;
        if (!hasOverride)
        {
            string icdRelative = Path.Combine("etc", "vulkan", "icd.d", "MoltenVK_icd.json");
            icd = BrewCandidates(prefix, "molten-vk", icdRelative,
                Path.Combine(prefix, "share", "vulkan", "icd.d", "MoltenVK_icd.json"), enumerateDirectories).FirstOrDefault(fileExists);
        }
        return new(loader ?? genericLoader, icd, false);
    }

    private static IEnumerable<string> BrewCandidates(string prefix, string formula, string relative,
        string linkedPath, Func<string, IEnumerable<string>> enumerateDirectories)
    {
        yield return Path.Combine(prefix, "opt", formula, relative);
        yield return linkedPath;
        // opt selects Homebrew's active version. If it is absent, keep Cellar
        // fallback deterministic instead of depending on directory enumeration order.
        foreach (var versionDir in enumerateDirectories(Path.Combine(prefix, "Cellar", formula))
            .OrderByDescending(path => path, StringComparer.Ordinal))
            yield return Path.Combine(versionDir, relative);
    }
}

internal readonly record struct MacVulkanResolution(string LoaderPath, string? IcdPath, bool DirectMoltenVk);

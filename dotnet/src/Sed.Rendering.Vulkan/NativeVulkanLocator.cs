using System.Runtime.InteropServices;

namespace Sed.Rendering.Vulkan;

/// <summary>
/// Resolves the native Vulkan loader and (on macOS) the MoltenVK ICD at runtime.
///
/// On Apple Silicon there is no system Vulkan. We rely on the Homebrew
/// <c>vulkan-loader</c> (libvulkan.dylib) dispatching to the <c>molten-vk</c>
/// ICD, which translates Vulkan to Metal. Neither is symlinked into a default
/// search path, so we discover them here and surface clear errors when missing.
/// </summary>
public static class NativeVulkanLocator
{
    /// <summary>Ensures the loader is discoverable and the MoltenVK ICD is configured. Idempotent.</summary>
    public static void Configure()
    {
        if (!OperatingSystem.IsMacOS())
            return; // Linux/Windows use the system loader on the default path.

        EnsureIcdFilenames();
        EnsureLoaderOnPath();
    }

    private static void EnsureIcdFilenames()
    {
        // The loader reads VK_DRIVER_FILES / VK_ICD_FILENAMES at vkCreateInstance time.
        if (HasEnv("VK_DRIVER_FILES") || HasEnv("VK_ICD_FILENAMES"))
            return;

        var icd = FindFirstExisting(
            BrewCellarGlob("molten-vk", "etc/vulkan/icd.d/MoltenVK_icd.json"),
            new[]
            {
                "/opt/homebrew/share/vulkan/icd.d/MoltenVK_icd.json",
                "/usr/local/share/vulkan/icd.d/MoltenVK_icd.json",
            });

        if (icd is not null)
            Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", icd);
    }

    private static void EnsureLoaderOnPath()
    {
        var loaderDir = Path.GetDirectoryName(ResolveLoaderPath());
        if (loaderDir is null)
            return;

        // Prepend the loader directory to DYLD_LIBRARY_PATH so Silk.NET's
        // dlopen("libvulkan.dylib") succeeds without an absolute path.
        const string key = "DYLD_LIBRARY_PATH";
        var existing = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(existing))
            Environment.SetEnvironmentVariable(key, loaderDir);
        else if (!existing.Split(':').Contains(loaderDir))
            Environment.SetEnvironmentVariable(key, $"{loaderDir}:{existing}");
    }

    /// <summary>Uses the native loader name for each platform; macOS also searches Homebrew.</summary>
    public static string ResolveLoaderPath()
    {
        if (OperatingSystem.IsWindows())
        {
            string systemLoader = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
            return File.Exists(systemLoader) ? systemLoader : "vulkan-1.dll";
        }
        if (OperatingSystem.IsLinux()) return "libvulkan.so.1";
        var loader = FindFirstExisting(
            BrewCellarGlob("vulkan-loader", "lib/libvulkan.dylib"),
            new[]
            {
                "/opt/homebrew/lib/libvulkan.dylib",
                "/opt/homebrew/opt/vulkan-loader/lib/libvulkan.dylib",
                "/usr/local/lib/libvulkan.dylib",
            });
        return loader ?? "vulkan";
    }

    private static bool HasEnv(string name) =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));

    private static string? FindFirstExisting(IEnumerable<string> globResults, IEnumerable<string> fixedPaths)
    {
        foreach (var p in globResults)
            if (File.Exists(p)) return p;
        foreach (var p in fixedPaths)
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Expands /opt/homebrew/Cellar/&lt;formula&gt;/*/&lt;relative&gt; across installed versions.</summary>
    private static IEnumerable<string> BrewCellarGlob(string formula, string relative)
    {
        foreach (var prefix in new[] { "/opt/homebrew/Cellar", "/usr/local/Cellar" })
        {
            var formulaDir = Path.Combine(prefix, formula);
            if (!Directory.Exists(formulaDir)) continue;
            foreach (var versionDir in Directory.EnumerateDirectories(formulaDir))
                yield return Path.Combine(versionDir, relative);
        }
    }
}

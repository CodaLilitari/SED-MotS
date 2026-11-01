using System.Runtime.InteropServices;
using Sed.Rendering.Vulkan;
using Silk.NET.Vulkan;

namespace Sed.Core.Tests;

public sealed class NativeVulkanLocatorTests
{
    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X64)]
    public void RelocatedBundleTakesPriorityWithoutConfiguringAnIcd(Architecture architecture)
    {
        string appDirectory = Path.Combine("/Volumes", "Moved Applications", "SED-MotS.app", "Contents", "MacOS");
        string bundled = Path.Combine(appDirectory, "libMoltenVK.dylib");
        var result = Resolve(appDirectory, architecture, [bundled, .. BothHomebrewInstallations()]);

        Assert.Equal(bundled, result.LoaderPath);
        Assert.True(result.DirectMoltenVk);
        Assert.Null(result.IcdPath);
    }

    [Theory]
    [InlineData(Architecture.Arm64, "/opt/homebrew")]
    [InlineData(Architecture.X64, "/usr/local")]
    public void HomebrewMatchesProcessArchitectureWhenBothInstallationsExist(Architecture architecture, string prefix)
    {
        var result = Resolve("/missing-bundle", architecture, BothHomebrewInstallations());

        Assert.Equal(Path.Combine(prefix, "opt", "vulkan-loader", "lib", "libvulkan.dylib"), result.LoaderPath);
        Assert.Equal(Path.Combine(prefix, "opt", "molten-vk", "etc", "vulkan", "icd.d", "MoltenVK_icd.json"), result.IcdPath);
        Assert.False(result.DirectMoltenVk);
    }

    [Theory]
    [InlineData("/no-driver.json", null)]
    [InlineData(null, "/no-driver.json")]
    [InlineData("/driver.json", "/legacy-driver.json")]
    public void ExplicitDriverOverrideDisablesBundledDriverAndAutomaticIcd(string? driverFiles, string? icdFilenames)
    {
        string appDirectory = Path.Combine("/Applications", "SED-MotS.app", "Contents", "MacOS");
        string bundled = Path.Combine(appDirectory, "libMoltenVK.dylib");
        var result = Resolve(appDirectory, Architecture.Arm64, [bundled, .. BothHomebrewInstallations()], driverFiles, icdFilenames);

        Assert.Equal(Path.Combine("/opt/homebrew", "opt", "vulkan-loader", "lib", "libvulkan.dylib"), result.LoaderPath);
        Assert.False(result.DirectMoltenVk);
        Assert.Null(result.IcdPath);
    }

    [Theory]
    [InlineData("/no-driver.json", null)]
    [InlineData(null, "/no-driver.json")]
    public void ExplicitOverrideWithoutGenericLoaderNeverFallsBackToBundledDriver(string? driverFiles, string? icdFilenames)
    {
        string bundled = Path.Combine("/bundle", "libMoltenVK.dylib");
        var result = Resolve("/bundle", Architecture.X64, [bundled], driverFiles, icdFilenames);

        Assert.Equal("libvulkan.1.dylib", result.LoaderPath);
        Assert.False(result.DirectMoltenVk);
        Assert.Null(result.IcdPath);
    }

    [Fact]
    public void EmptyOverridesDoNotDisableBundledDriver()
    {
        string bundled = Path.Combine("/bundle", "libMoltenVK.dylib");
        var result = Resolve("/bundle", Architecture.X64, [bundled], "", "");

        Assert.Equal(bundled, result.LoaderPath);
        Assert.True(result.DirectMoltenVk);
        Assert.Null(result.IcdPath);
    }

    [Theory]
    [InlineData(Architecture.Arm64, "/usr/local")]
    [InlineData(Architecture.X64, "/opt/homebrew")]
    public void WrongArchitectureHomebrewIsNotAUsableFallback(Architecture architecture, string wrongPrefix)
    {
        var result = Resolve("/missing-bundle", architecture, HomebrewInstallation(wrongPrefix));

        Assert.Equal("libvulkan.1.dylib", result.LoaderPath);
        Assert.Null(result.IcdPath);
    }

    [Fact]
    public void StableOptPathsWinOverLinkedAndVersionedHomebrewFiles()
    {
        string loaderCellar = Path.Combine("/opt/homebrew", "Cellar", "vulkan-loader");
        string driverCellar = Path.Combine("/opt/homebrew", "Cellar", "molten-vk");
        string loaderVersion = Path.Combine(loaderCellar, "9.0");
        string driverVersion = Path.Combine(driverCellar, "9.0");
        string[] files =
        [
            .. HomebrewInstallation("/opt/homebrew"),
            Path.Combine("/opt/homebrew", "lib", "libvulkan.dylib"),
            Path.Combine("/opt/homebrew", "share", "vulkan", "icd.d", "MoltenVK_icd.json"),
            Path.Combine(loaderVersion, "lib", "libvulkan.dylib"),
            Path.Combine(driverVersion, "etc", "vulkan", "icd.d", "MoltenVK_icd.json"),
        ];
        var directories = new Dictionary<string, string[]>
        {
            [loaderCellar] = [loaderVersion],
            [driverCellar] = [driverVersion],
        };
        var result = Resolve("/missing-bundle", Architecture.Arm64, files, directories: directories);

        Assert.Equal(Path.Combine("/opt/homebrew", "opt", "vulkan-loader", "lib", "libvulkan.dylib"), result.LoaderPath);
        Assert.Equal(Path.Combine("/opt/homebrew", "opt", "molten-vk", "etc", "vulkan", "icd.d", "MoltenVK_icd.json"), result.IcdPath);
    }

    [Fact]
    public void CellarFallbackSkipsMissingFilesAndDoesNotDependOnDirectoryEnumerationOrder()
    {
        string cellar = Path.Combine("/usr/local", "Cellar", "vulkan-loader");
        string oldVersion = Path.Combine(cellar, "1.0");
        string newVersion = Path.Combine(cellar, "2.0");
        string absentVersion = Path.Combine(cellar, "3.0");
        string[] files = [Path.Combine(oldVersion, "lib", "libvulkan.dylib"), Path.Combine(newVersion, "lib", "libvulkan.dylib")];
        var directories = new Dictionary<string, string[]> { [cellar] = [oldVersion, absentVersion, newVersion] };
        var first = Resolve("/missing-bundle", Architecture.X64, files, directories: directories);
        directories[cellar] = [newVersion, oldVersion, absentVersion];
        var second = Resolve("/missing-bundle", Architecture.X64, files, directories: directories);

        Assert.Equal(Path.Combine(newVersion, "lib", "libvulkan.dylib"), first.LoaderPath);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(Architecture.Arm64)]
    [InlineData(Architecture.X64)]
    public void MissingLibrariesLeaveDiscoveryToTheGenericLoader(Architecture architecture)
    {
        var result = Resolve("/missing-bundle", architecture, []);

        Assert.Equal("libvulkan.1.dylib", result.LoaderPath);
        Assert.False(result.DirectMoltenVk);
        Assert.Null(result.IcdPath);
    }

    [Theory]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.X86)]
    public void UnsupportedArchitectureDoesNotGuessAnInstalledLibrary(Architecture architecture)
    {
        var result = Resolve("/bundle", architecture, [Path.Combine("/bundle", "libMoltenVK.dylib"), .. BothHomebrewInstallations()]);

        Assert.Equal("libvulkan.1.dylib", result.LoaderPath);
        Assert.False(result.DirectMoltenVk);
        Assert.Null(result.IcdPath);
    }

    [Fact]
    public void AdvertisedMacPortabilityEnablesExtensionAndFlagTogether()
    {
        var result = VulkanContext.SelectInstanceExtensions(true, ["VK_KHR_surface", "VK_KHR_portability_enumeration"]);

        Assert.Equal(new[] { "VK_KHR_portability_enumeration" }, result.Names);
        Assert.Equal(InstanceCreateFlags.EnumeratePortabilityBitKhr, result.Flags);
    }

    [Theory]
    [InlineData(true, "VK_KHR_surface")]
    [InlineData(true, "vk_khr_portability_enumeration")]
    [InlineData(false, "VK_KHR_portability_enumeration")]
    public void UnsupportedOrNonMacPortabilityDoesNotEnableExtensionOrFlag(bool isMacOS, string advertisedExtension)
    {
        var result = VulkanContext.SelectInstanceExtensions(isMacOS, [advertisedExtension]);

        Assert.Empty(result.Names);
        Assert.Equal(InstanceCreateFlags.None, result.Flags);
    }

    private static string[] HomebrewInstallation(string prefix) =>
    [
        Path.Combine(prefix, "opt", "vulkan-loader", "lib", "libvulkan.dylib"),
        Path.Combine(prefix, "opt", "molten-vk", "etc", "vulkan", "icd.d", "MoltenVK_icd.json"),
    ];

    private static string[] BothHomebrewInstallations() =>
        [.. HomebrewInstallation("/opt/homebrew"), .. HomebrewInstallation("/usr/local")];

    private static MacVulkanResolution Resolve(string baseDirectory, Architecture architecture, string[] files,
        string? driverFiles = null, string? icdFilenames = null, Dictionary<string, string[]>? directories = null)
    {
        var existingFiles = new HashSet<string>(files, StringComparer.Ordinal);
        return NativeVulkanLocator.ResolveMacConfiguration(baseDirectory, architecture, driverFiles, icdFilenames,
            existingFiles.Contains, path => directories is not null && directories.TryGetValue(path, out var children) ? children : []);
    }
}

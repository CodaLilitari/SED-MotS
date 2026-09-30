using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sed.Core.Updating;

namespace Sed.Core.Tests;

public sealed class PortableUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sed-update-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Feed = new("https://updates.example/releases.json");
    private static readonly Uri Package = new("https://updates.example/release.zip");
    private static UpdateUrlPolicy Policy() => new([new Uri("https://updates.example/")]);
    public PortableUpdateTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Theory]
    [InlineData("0.4.0-mots.9-dev", "0.4.0-mots.10-dev")]
    [InlineData("0.4.0-mots.99-dev", "0.4.0")]
    [InlineData("0.4.0", "0.4.1-mots.1-dev")]
    public void VersionsOrderNumericallyAndStableFollowsPreview(string earlier, string later)
        => Assert.True(SedReleaseVersion.Parse(earlier).CompareTo(SedReleaseVersion.Parse(later)) < 0);

    [Theory]
    [InlineData("0.4")][InlineData("0.4.0-mots.01-dev")][InlineData("0.4.0+command")][InlineData("1.2.3.4")][InlineData("999999999999999.0.0")]
    [InlineData("0.4.0-mots.16-dev\n")]
    public void UnsupportedVersionsAreRejected(string version) => Assert.Throws<InvalidDataException>(() => SedReleaseVersion.Parse(version));

    [Fact]
    public void StableNeverSelectsPreviewAndEqualVersionNeverReinstalls()
    {
        var releases = UpdateManifest.Parse(Manifest("0.4.0-mots.16-dev"), Policy());
        Assert.Null(UpdateManifest.Select(releases, "0.4.0-mots.15-dev", "stable"));
        Assert.NotNull(UpdateManifest.Select(releases, "0.4.0-mots.15-dev", "preview"));
        Assert.Null(UpdateManifest.Select(releases, "0.4.0-mots.16-dev", "preview"));
        Assert.Null(UpdateManifest.Select(releases, "0.4.0-mots.17-dev", "preview"));
    }

    [Theory]
    [InlineData("http://updates.example/release.zip")]
    [InlineData("https://evil.example/release.zip")]
    [InlineData("https://user@updates.example/release.zip")]
    [InlineData("https://updates.example:444/release.zip")]
    [InlineData("https://updates.example/release.zip?x=1")]
    [InlineData("https://updates.example/%2e%2e/secret")]
    public void UrlPolicyRejectsOutsideHostsAndAmbiguousUrls(string url)
        => Assert.Throws<InvalidDataException>(() => Policy().Validate(new Uri(url)));

    [Fact]
    public void GitLabMultiTenantPathsAreNotInterchangeable()
    {
        var policy = new UpdateUrlPolicy([new Uri("https://gitlab.com/api/v4/projects/86965025/packages/generic/sed-mots/")]);
        policy.Validate(new("https://gitlab.com/api/v4/projects/86965025/packages/generic/sed-mots/1/a.zip"));
        Assert.Throws<InvalidDataException>(() => policy.Validate(new("https://gitlab.com/api/v4/projects/1/packages/generic/sed-mots/1/a.zip")));
        Assert.Throws<InvalidDataException>(() => policy.Validate(new("https://gitlab.com/api/v4/projects/86965025/packages/generic/other/1/a.zip")));
    }

    [Fact]
    public void ManifestRejectsDuplicatePropertiesAndReleaseReplacement()
    {
        string json = Encoding.UTF8.GetString(Manifest());
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")), Policy()));
        var release = JsonDocument.Parse(json).RootElement.GetProperty("releases")[0].GetRawText();
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"releases\":[" + release + "," + release.Replace(new string('a', 64), new string('b', 64)) + "]}"), Policy()));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(new byte[UpdateManifest.MaxBytes + 1], Policy()));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(json.Replace("\"preview\"", "\"stable\"")), Policy()));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(json.Replace("\"sizeBytes\":123", "\"sizeBytes\":0")), Policy()));
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(json.Replace(new string('a', 64), new string('a', 64) + "\\n")), Policy()));
    }

    [Fact]
    public async Task FailedPreferredFeedFallsBackAndDailyCachePreventsRepeatedRequests()
    {
        var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/bad.json"
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Bytes(Manifest()));
        using var http = new HttpClient(handler);
        var service = new PortableUpdateService(http, Policy(), [new("https://updates.example/bad.json"), Feed], Path.Combine(_root, "cache.json"));
        var first = await service.CheckAsync("0.4.0-mots.15-dev", "preview", false);
        Assert.NotNull(first.Available); Assert.False(first.Cached); Assert.Equal(2, handler.Count);
        var second = await service.CheckAsync("0.4.0-mots.15-dev", "preview", false);
        Assert.NotNull(second.Available); Assert.True(second.Cached); Assert.Equal(2, handler.Count);
        var equal = await service.CheckAsync("0.4.0-mots.16-dev", "preview", false);
        Assert.Null(equal.Available); Assert.Equal(2, handler.Count);
        await service.CheckAsync("0.4.0-mots.15-dev", "preview", true); Assert.Equal(4, handler.Count);
    }

    [Fact]
    public async Task CorruptCachedManifestFallsThroughAndInvalidCurrentVersionMakesNoRequests()
    {
        var handler = new Handler(_ => Bytes(Manifest())); using var http = new HttpClient(handler);
        string cache = Path.Combine(_root, "cache.json");
        File.WriteAllBytes(cache, JsonSerializer.SerializeToUtf8Bytes(new { CheckedUtc = DateTimeOffset.UtcNow, Feed = Feed.AbsoluteUri, Manifest = Encoding.UTF8.GetBytes("{broken"), Error = (string?)null }));
        var service = Service(http);
        Assert.NotNull((await service.CheckAsync("0.4.0-mots.15-dev", "preview", false)).Available);
        Assert.Equal(1, handler.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync("unknown", "preview", true));
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RedirectOutsideProjectIsRejectedBeforeItsRequest()
    {
        var handler = new Handler(_ => { var r = new HttpResponseMessage(HttpStatusCode.Redirect); r.Headers.Location = new("https://elsewhere.example/x"); return r; });
        using var http = new HttpClient(handler);
        var service = new PortableUpdateService(http, Policy(), [Feed], Path.Combine(_root, "cache.json"));
        var result = await service.CheckAsync("0.4.0-mots.15-dev", "preview", true);
        Assert.Null(result.Available); Assert.Contains("outside", result.Message); Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task OversizeFeedFailsBoundedAndCancelledStagePublishesNothing()
    {
        using var feedHttp = new HttpClient(new Handler(_ => Bytes(new byte[UpdateManifest.MaxBytes + 1])));
        Assert.Contains("exceeds", (await Service(feedHttp).CheckAsync("0.4.0-mots.15-dev", "preview", true)).Message);
        string old = Old(); byte[] zip = Zip();
        using var http = new HttpClient(new Handler(_ => Bytes(zip)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(http).StageAsync(Release(zip), old, Path.Combine(_root, "fallback"), cancellation.Token));
        Assert.Equal(new[] { old }, Directory.GetDirectories(_root));
        Assert.Equal("unsaved user's work", File.ReadAllText(Path.Combine(old, "my-level.jkl")));
    }

    [Fact]
    public async Task StagesNewInstallationPreservingOldApplicationAndUserFiles()
    {
        string old = Old(); byte[] zip = Zip();
        using var http = new HttpClient(new Handler(_ => Bytes(zip)));
        var staged = await Service(http).StageAsync(Release(zip), old, Path.Combine(_root, "fallback"));
        Assert.NotEqual(old, staged.Directory);
        Assert.Equal("old executable", File.ReadAllText(Path.Combine(old, "Sed.App.exe")));
        Assert.Equal("unsaved user's work", File.ReadAllText(Path.Combine(old, "my-level.jkl")));
        Assert.Equal("new executable", File.ReadAllText(staged.Executable));
        await PortableUpdateService.VerifyStagedAsync(staged);
        Assert.Empty(Directory.GetDirectories(_root, ".sed-update-*"));
    }

    [Fact]
    public async Task UnwritablePreferredParentUsesExplicitFallback()
    {
        string nonDirectory = Path.Combine(_root, "parent-file"); File.WriteAllText(nonDirectory, "keep");
        byte[] zip = Zip(); using var http = new HttpClient(new Handler(_ => Bytes(zip)));
        string fallback = Path.Combine(_root, "fallback");
        var staged = await Service(http).StageAsync(Release(zip), Path.Combine(nonDirectory, "old"), fallback);
        Assert.Equal(fallback, Path.GetDirectoryName(staged.Directory)); Assert.Equal("keep", File.ReadAllText(nonDirectory));
    }

    [Theory]
    [InlineData("hash")][InlineData("short")][InlineData("extra")][InlineData("channel")]
    public async Task BadDownloadsAndDirectCallChannelMismatchLeaveOldIntact(string fault)
    {
        string old = Old(); byte[] zip = Zip(); var release = Release(zip);
        if (fault == "hash") release = release with { Sha256 = new string('a', 64) };
        if (fault == "short") release = release with { SizeBytes = zip.Length + 1 };
        if (fault == "extra") release = release with { SizeBytes = zip.Length - 1 };
        if (fault == "channel") release = release with { Channel = "stable" };
        using var http = new HttpClient(new Handler(_ => Bytes(zip)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).StageAsync(release, old, Path.Combine(_root, "fallback")));
        Assert.Equal("old executable", File.ReadAllText(Path.Combine(old, "Sed.App.exe")));
        Assert.Equal("unsaved user's work", File.ReadAllText(Path.Combine(old, "my-level.jkl")));
        Assert.Empty(Directory.GetDirectories(_root, ".sed-update-*"));
    }

    [Theory]
    [InlineData("../escape.dll")][InlineData("root/../../escape.dll")][InlineData("root/C:evil.dll")]
    [InlineData("root/back\\slash.dll")][InlineData("root/NUL.txt")][InlineData("root/trailing. ")]
    [InlineData("/absolute.dll")][InlineData("root/Sed.App.exe")][InlineData("root/.sed-update-receipt.json")]
    public async Task UnsafeZipIsRefusedWithoutPublishingPartialInstall(string name)
    {
        string old = Old(); byte[] zip = Zip(name);
        using var http = new HttpClient(new Handler(_ => Bytes(zip)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).StageAsync(Release(zip), old, Path.Combine(_root, "fallback")));
        Assert.Equal(new[] { old }, Directory.GetDirectories(_root));
        Assert.False(File.Exists(Path.Combine(_root, "escape.dll")));
    }

    [Fact]
    public async Task ZipSymlinksAndFileDirectoryCollisionsAreRefused()
    {
        string old = Old(); byte[] zip = Zip("root/link", symlink: true);
        using var http = new HttpClient(new Handler(_ => Bytes(zip)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).StageAsync(Release(zip), old, Path.Combine(_root, "fallback")));
        zip = Zip("root/Sed.App.exe/nested.dll");
        using var http2 = new HttpClient(new Handler(_ => Bytes(zip)));
        await Assert.ThrowsAnyAsync<IOException>(() => Service(http2).StageAsync(Release(zip), old, Path.Combine(_root, "fallback")));
        Assert.Equal(new[] { old }, Directory.GetDirectories(_root));
    }

    [Theory]
    [InlineData("exe")][InlineData("extra")][InlineData("receipt")]
    public async Task ChangedPreparedInstallationIsRejectedBeforeLaunch(string change)
    {
        string old = Old(); byte[] zip = Zip(); using var http = new HttpClient(new Handler(_ => Bytes(zip)));
        var staged = await Service(http).StageAsync(Release(zip), old, Path.Combine(_root, "fallback"));
        File.WriteAllText(change switch { "exe" => staged.Executable, "extra" => Path.Combine(staged.Directory, "injected.dll"), _ => staged.Receipt }, "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => PortableUpdateService.VerifyStagedAsync(staged));
    }

    private string Old()
    {
        string old = Path.Combine(_root, "old"); Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "Sed.App.exe"), "old executable");
        File.WriteAllText(Path.Combine(old, "my-level.jkl"), "unsaved user's work"); return old;
    }
    private PortableUpdateService Service(HttpClient http) => new(http, Policy(), [Feed], Path.Combine(_root, "cache.json"));
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static UpdateRelease Release(byte[] bytes) => new("0.4.0-mots.16-dev", "preview", "win-x64", Package,
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, "root");
    private static byte[] Manifest(string version = "0.4.0-mots.16-dev") => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1,
        releases = new[] { new { version, channel = "preview", platform = "win-x64", packageUrl = Package.AbsoluteUri, sha256 = new string('a', 64), sizeBytes = 123, entryDirectory = "root" } },
    });
    private static byte[] Zip(string? extra = null, bool symlink = false)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (string name in new[] { "Sed.App.exe", "Sed.App.dll", "Sed.App.runtimeconfig.json", "coreclr.dll" })
            { using var writer = new StreamWriter(zip.CreateEntry("root/" + name).Open()); writer.Write(name == "Sed.App.exe" ? "new executable" : "fixture"); }
            if (extra is not null)
            {
                var entry = zip.CreateEntry(extra);
                if (symlink) entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                using var writer = new StreamWriter(entry.Open()); writer.Write("outside");
            }
        }
        return bytes.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Task.FromResult(response(request)); }
    }
}

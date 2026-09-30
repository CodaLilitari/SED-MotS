using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Sed.Core.Updating;

// Core-only release acceptance tool. No Avalonia, user settings, or editor startup code is loaded.
// Normal mode never launches the downloaded editor. Only --self-test launches our original fixture.
return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
    {
        if (args is ["--fake-app", var marker]) { await File.WriteAllTextAsync(marker, "original updater fixture started"); return 0; }
        if (args is ["--self-test", var root]) return await SelfTestAsync(root);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (i + 1 >= args.Length || !options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Expected unique --name value pairs.");
        string Required(string key) => options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"Missing {key}.");
        var allowed = new[] { "--feed", "--current-version", "--channel", "--current-dir", "--install-root", "--result", "--manifest-file", "--package-file" };
        if (options.Keys.Any(k => !allowed.Contains(k))) throw new ArgumentException("Unknown option.");
        Uri feed = new(Required("--feed"));
        string current = Path.GetFullPath(Required("--current-dir")), install = Path.GetFullPath(Required("--install-root"));
        string result = Path.GetFullPath(Required("--result"));
        using var handler = options.ContainsKey("--manifest-file") || options.ContainsKey("--package-file")
            ? Offline(feed, Required("--manifest-file"), Required("--package-file"), Required("--current-version"), Required("--channel"))
            : (HttpMessageHandler)new HttpClientHandler { AllowAutoRedirect = false };
        var report = await ProbeAsync(handler, feed, Required("--current-version"), Required("--channel"), current, install);
        await SaveReportAsync(result, report);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine($"Updater probe failed: {ex.GetType().Name}: {ex.Message}"); return 1; }
}

static UpdateUrlPolicy Policy() => new([
    new("https://richard-pemberton-research.darkahn.chatgpt.site/updates/sed/"),
    new("https://gitlab.com/codalilitari/SED-MotS/-/raw/main/updates/sed-releases.json"),
    new("https://gitlab.com/api/v4/projects/86965025/packages/generic/sed-mots/")]);

static HttpMessageHandler Offline(Uri feed, string manifestPath, string packagePath, string current, string channel)
{
    Policy().Validate(feed);
    if (new FileInfo(manifestPath).Length > UpdateManifest.MaxBytes) throw new InvalidDataException("Manifest exceeds bounded size.");
    byte[] bytes = File.ReadAllBytes(manifestPath);
    var release = UpdateManifest.Select(UpdateManifest.Parse(bytes, Policy()), current, channel)
        ?? throw new InvalidDataException("The probe needs a strictly newer release.");
    return new FileHandler(feed, bytes, release.PackageUrl, Path.GetFullPath(packagePath));
}

static async Task<ProbeReport> ProbeAsync(HttpMessageHandler handler, Uri feed, string version, string channel, string current, string install)
{
    if (!Directory.Exists(current)) throw new DirectoryNotFoundException("Use an existing disposable current directory containing preservation markers.");
    Directory.CreateDirectory(install);
    var before = await SnapshotAsync(current);
    using var http = new HttpClient(handler, false) { Timeout = Timeout.InfiniteTimeSpan };
    var service = new PortableUpdateService(http, Policy(), [feed], Path.Combine(install, "probe-cache-" + Guid.NewGuid().ToString("N") + ".json"));
    var check = await service.CheckAsync(version, channel, true);
    var release = check.Available ?? throw new InvalidDataException(check.Message);
    var staged = await service.StageAsync(release, current, install);
    await PortableUpdateService.VerifyStagedAsync(staged);
    var after = await SnapshotAsync(current);
    if (!before.OrderBy(p => p.Key).SequenceEqual(after.OrderBy(p => p.Key))) throw new InvalidDataException("Old installation contents changed.");
    var equal = await service.CheckAsync(release.Version, channel, false);
    if (equal.Available is not null) throw new InvalidDataException("Equal-version release was offered again.");
    return new(version, release.Version, release.PackageUrl.AbsoluteUri, release.Sha256, release.SizeBytes,
        staged.Directory, staged, before.Count, true, true, false);
}

static async Task<Dictionary<string, string>> SnapshotAsync(string root)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var pending = new Stack<string>(); pending.Push(root);
    while (pending.TryPop(out string? directory))
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Probe marker trees must not contain links.");
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Probe marker trees must not contain links.");
            if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
            await using var input = File.OpenRead(path);
            result.Add(Path.GetRelativePath(root, path), Convert.ToHexString(await SHA256.HashDataAsync(input)));
        }
    }
    return result;
}

static async Task<int> SelfTestAsync(string requestedRoot)
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Executable fixture integration is Windows-only.");
    string root = Path.GetFullPath(requestedRoot);
    if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Self-test needs a new disposable output directory.");
    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"))) throw new IOException("Publish this probe self-contained for win-x64 before --self-test.");
    Directory.CreateDirectory(root);
    string current = Path.Combine(root, "old"); Directory.CreateDirectory(current);
    File.WriteAllText(Path.Combine(current, "Sed.App.exe"), "old application preserved");
    File.WriteAllText(Path.Combine(current, "user-level.jkl"), "user work preserved");
    string package = Path.Combine(root, "original-fixture.zip");
    ZipFile.CreateFromDirectory(AppContext.BaseDirectory, package, CompressionLevel.Fastest, false);
    string hash; await using (var input = File.OpenRead(package)) hash = Convert.ToHexString(await SHA256.HashDataAsync(input));
    Uri feed = new("https://richard-pemberton-research.darkahn.chatgpt.site/updates/sed/releases.json");
    Uri url = new("https://gitlab.com/api/v4/projects/86965025/packages/generic/sed-mots/0.4.0-mots.16-dev/original-fixture.zip");
    string manifest = Path.Combine(root, "fixture.json");
    await File.WriteAllBytesAsync(manifest, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, releases = new[] { new {
        version = "0.4.0-mots.16-dev", channel = "preview", platform = "win-x64", packageUrl = url.AbsoluteUri,
        sha256 = hash, sizeBytes = new FileInfo(package).Length, entryDirectory = "" } } }));
    using var handler = Offline(feed, manifest, package, "0.4.0-mots.15-dev", "preview");
    var report = await ProbeAsync(handler, feed, "0.4.0-mots.15-dev", "preview", current, root);
    string marker = Path.Combine(root, "launched.txt");
    using var process = await PortableUpdateService.StartVerifiedAsync(report.Staged, ["--fake-app", marker]);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await process.WaitForExitAsync(timeout.Token);
    if (process.ExitCode != 0 || !File.Exists(marker) || File.ReadAllText(marker) != "original updater fixture started")
        throw new IOException("Original fixture did not start and write its marker.");
    report = report with { OriginalFixtureLaunchVerified = true };
    await SaveReportAsync(Path.Combine(root, "result.json"), report);
    Console.WriteLine(JsonSerializer.Serialize(report));
    return 0;
}

static async Task SaveReportAsync(string path, ProbeReport report)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
}

sealed record ProbeReport(string PreviousVersion, string NewVersion, string PackageUrl, string PackageSha256, long PackageBytes,
    string InstallDirectory, StagedUpdate Staged, int PreservedFileCount, bool OldFilesUnchanged, bool EqualVersionRefused,
    bool OriginalFixtureLaunchVerified);

sealed class FileHandler(Uri feed, byte[] manifest, Uri package, string packagePath) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpContent content = request.RequestUri == feed ? new ByteArrayContent(manifest)
            : request.RequestUri == package ? new StreamContent(File.OpenRead(packagePath))
            : throw new InvalidDataException("Offline probe received an unexpected URL; no network request made.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}

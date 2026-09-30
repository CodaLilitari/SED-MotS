using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sed.Core.Updating;

public sealed record UpdateCheck(UpdateRelease? Available, string Message, bool Cached);
public sealed record StagedUpdate(string Directory, string Executable, UpdateRelease Release, string Receipt, string ReceiptSha256);

/// <summary>Bounded HTTPS acquisition and a new portable installation. Never edits the running installation.</summary>
public sealed class PortableUpdateService(HttpClient http, UpdateUrlPolicy urls, IReadOnlyList<Uri> feeds, string cachePath)
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private sealed record Cache(DateTimeOffset CheckedUtc, string Feed, byte[]? Manifest, string? Error);
    private sealed record InstalledFile(string Path, long Length, string Sha256);
    private sealed record InstallReceipt(string Version, string PackageSha256, List<InstalledFile> Files);

    public async Task<UpdateCheck> CheckAsync(string version, string channel, bool force, CancellationToken cancellation = default)
    {
        // Invalid local identity cannot select a safe upgrade, even from a previously cached feed.
        _ = UpdateManifest.Select([], version, channel);
        await _checkLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (!force && ReadCache() is { } cached && cached.CheckedUtc <= DateTimeOffset.UtcNow
                && DateTimeOffset.UtcNow - cached.CheckedUtc < CheckInterval)
            {
                if (cached.Manifest is not null && feeds.Any(f => f.AbsoluteUri == cached.Feed))
                {
                    try { return Result(UpdateManifest.Select(UpdateManifest.Parse(cached.Manifest, urls), version, channel), true); }
                    catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or FormatException or OverflowException)
                    { /* A corrupt cache is a miss; retry the configured feeds. */ }
                }
                if (cached.Error is not null) return new(null, cached.Error, true);
            }
            string error = "No update feed is configured.";
            foreach (var feed in feeds)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    using var response = await GetAsync(feed, "application/json", timeout.Token).ConfigureAwait(false);
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var memory = new MemoryStream();
                    await CopyBoundedAsync(stream, memory, UpdateManifest.MaxBytes, timeout.Token).ConfigureAwait(false);
                    var bytes = memory.ToArray();
                    var candidate = UpdateManifest.Select(UpdateManifest.Parse(bytes, urls), version, channel);
                    WriteCache(new(DateTimeOffset.UtcNow, feed.AbsoluteUri, bytes, null));
                    return Result(candidate, false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellation.IsCancellationRequested)
                { error = $"Update check unavailable: {ex.Message}"; }
            }
            cancellation.ThrowIfCancellationRequested();
            WriteCache(new(DateTimeOffset.UtcNow, "", null, error));
            return new(null, error, false);
        }
        finally { _checkLock.Release(); }
    }

    private static UpdateCheck Result(UpdateRelease? release, bool cached) => new(release,
        release is null ? "This version is up to date on the selected channel." : $"SED {release.Version} is available.", cached);

    private Cache? ReadCache()
    {
        try
        {
            var file = new FileInfo(cachePath);
            return file.Exists && file.Length <= 2 * UpdateManifest.MaxBytes
                ? JsonSerializer.Deserialize<Cache>(File.ReadAllBytes(cachePath)) : null;
        }
        catch { return null; }
    }
    private void WriteCache(Cache cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))!);
            string temp = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(cache));
            File.Move(temp, cachePath, true);
        }
        catch { /* A read-only profile must not stop the editor or the check. */ }
    }

    public async Task<StagedUpdate> StageAsync(UpdateRelease release, string currentDirectory, string fallbackDirectory,
        CancellationToken cancellation = default)
    {
        // Revalidate even when supplied by a local cache/caller rather than the feed parser.
        var version = SedReleaseVersion.Parse(release.Version);
        urls.Validate(release.PackageUrl);
        if (release.Channel is not ("stable" or "preview") || (release.Channel == "stable") != (version.Preview is null)
            || release.Platform != "win-x64" || release.SizeBytes <= 0 || release.SizeBytes > UpdateManifest.MaxPackageBytes
            || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit)
            || (release.EntryDirectory.Length != 0 && !SafeUpdatePath.IsComponent(release.EntryDirectory)))
            throw new InvalidDataException("Invalid portable Windows release.");
        string current = Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string preferred = Path.GetDirectoryName(current) ?? throw new IOException("Current installation has no parent.");
        string stage;
        try { stage = CreateStage(preferred); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { stage = CreateStage(fallbackDirectory); }
        try
        {
            string zip = Path.Combine(stage, "download.zip");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            using (var response = await GetAsync(release.PackageUrl, "application/zip", timeout.Token).ConfigureAwait(false))
            {
                if (response.Content.Headers.ContentLength is { } size && size != release.SizeBytes)
                    throw new InvalidDataException("Download size does not match the release manifest.");
                await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
                long count = await CopyBoundedAsync(source, output, release.SizeBytes, timeout.Token).ConfigureAwait(false);
                if (count != release.SizeBytes) throw new InvalidDataException("Download is truncated.");
            }
            await using (var input = File.OpenRead(zip))
            {
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, timeout.Token).ConfigureAwait(false));
                if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Download SHA256 does not match the release manifest.");
            }
            string payload = Path.Combine(stage, "payload");
            Directory.CreateDirectory(payload);
            var files = await ExtractAsync(zip, payload, release.EntryDirectory, timeout.Token).ConfigureAwait(false);
            foreach (string required in new[] { "Sed.App.exe", "Sed.App.dll", "Sed.App.runtimeconfig.json", "coreclr.dll" })
                if (!files.Any(f => f.Path == required && f.Length > 0)) throw new InvalidDataException($"Portable release is missing {required}.");
            string receiptName = ".sed-update-receipt.json";
            byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(new InstallReceipt(release.Version, release.Sha256, files));
            File.WriteAllBytes(Path.Combine(payload, receiptName), receiptBytes);
            // Destination is never overwritten; the random suffix also avoids treating old/user-modified installs as valid caches.
            string destination = Path.Combine(Path.GetDirectoryName(stage)!, $"SED-MotS-{release.Version}-{Guid.NewGuid():N}");
            timeout.Token.ThrowIfCancellationRequested();
            Directory.Move(payload, destination);
            return new(destination, Path.Combine(destination, "Sed.App.exe"), release, Path.Combine(destination, receiptName), Convert.ToHexString(SHA256.HashData(receiptBytes)));
        }
        finally
        {
            // Only our exact newly-created staging directory is eligible for cleanup. Never the current or completed installation.
            try { SafeUpdatePath.RejectLinks(stage); Directory.Delete(stage, true); } catch { /* Interrupted downloads can be cleaned manually. */ }
        }
    }

    private static string CreateStage(string parent)
    {
        parent = Path.GetFullPath(parent);
        SafeUpdatePath.RejectLinks(parent);
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, ".sed-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        return stage;
    }

    private static async Task<List<InstalledFile>> ExtractAsync(string zipPath, string destination, string entryDirectory, CancellationToken cancellation)
    {
        const long maxExpanded = 4L * 1024 * 1024 * 1024;
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count is 0 or > 20000) throw new InvalidDataException("ZIP entry count exceeds the supported limit.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plan = new List<(ZipArchiveEntry Entry, string Relative)>();
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            // Reject aliases, symlinks and special files, including Windows reparse-point metadata.
            string name = entry.FullName;
            bool directory = name.EndsWith('/');
            var parts = name.TrimEnd('/').Split('/');
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (name.Length > 240 || name.Contains('\\') || parts.Any(p => !SafeUpdatePath.IsComponent(p))
                || !names.Add(name.TrimEnd('/')) || (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP contains an unsafe, duplicate or unsupported path.");
            if (entryDirectory.Length > 0)
            {
                if (parts[0] != entryDirectory) throw new InvalidDataException("ZIP entries lie outside the declared package root.");
                parts = parts.Skip(1).ToArray();
            }
            if (parts.Length == 0 && directory) continue;
            if (parts.Length == 0) throw new InvalidDataException("Invalid package root entry.");
            string relative = string.Join('/', parts);
            if (relative.Equals(".sed-update-receipt.json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP uses a reserved updater path.");
            if (directory) continue;
            if (entry.Length < 0 || entry.Length > UpdateManifest.MaxPackageBytes || (total += entry.Length) > maxExpanded)
                throw new InvalidDataException("ZIP expanded size exceeds the supported limit.");
            plan.Add((entry, relative));
        }
        var result = new List<InstalledFile>();
        foreach (var (entry, relative) in plan)
        {
            string path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var source = entry.Open())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                long count = await CopyBoundedAsync(source, output, entry.Length, cancellation).ConfigureAwait(false);
                if (count != entry.Length) throw new InvalidDataException("ZIP entry is truncated.");
            }
            await using var check = File.OpenRead(path);
            result.Add(new(relative, entry.Length, Convert.ToHexString(await SHA256.HashDataAsync(check, cancellation).ConfigureAwait(false))));
        }
        return result;
    }

    /// <summary>Recheck every installed file immediately before offering a launch. Paths/arguments never come from commands in a feed.</summary>
    public static async Task VerifyStagedAsync(StagedUpdate staged, CancellationToken cancellation = default)
    {
        SafeUpdatePath.RejectLinks(staged.Directory);
        if (Path.GetFullPath(staged.Executable) != Path.Combine(Path.GetFullPath(staged.Directory), "Sed.App.exe")
            || Path.GetFullPath(staged.Receipt) != Path.Combine(Path.GetFullPath(staged.Directory), ".sed-update-receipt.json"))
            throw new InvalidDataException("Invalid staged installation identity.");
        var info = new FileInfo(staged.Receipt);
        if (!info.Exists || info.Length > 8 * 1024 * 1024 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Missing or invalid update receipt.");
        byte[] receiptBytes = await File.ReadAllBytesAsync(staged.Receipt, cancellation).ConfigureAwait(false);
        if (Convert.ToHexString(SHA256.HashData(receiptBytes)) != staged.ReceiptSha256) throw new InvalidDataException("Staged verification receipt changed.");
        var receipt = JsonSerializer.Deserialize<InstallReceipt>(receiptBytes)
            ?? throw new InvalidDataException("Invalid update receipt.");
        if (receipt.Version != staged.Release.Version || receipt.PackageSha256 != staged.Release.Sha256 || receipt.Files.Count is 0 or > 20000)
            throw new InvalidDataException("Staged release identity changed.");
        foreach (var file in receipt.Files)
        {
            if (file.Path.Split('/').Any(p => !SafeUpdatePath.IsComponent(p))) throw new InvalidDataException("Invalid staged file path.");
            string path = Path.Combine(staged.Directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            SafeUpdatePath.RejectLinks(Path.GetDirectoryName(path)!);
            var actual = new FileInfo(path);
            if (!actual.Exists || actual.Length != file.Length || (actual.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A staged file is missing or changed.");
            await using var input = File.OpenRead(path);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false));
            if (hash != file.Sha256) throw new InvalidDataException("A staged file failed SHA256 verification.");
        }
        // Extra executable/DLL/plugin files are not part of the verified release either.
        var expected = new HashSet<string>(receipt.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase) { ".sed-update-receipt.json" };
        foreach (string file in EnumerateSafeFiles(staged.Directory))
            if (!expected.Remove(Path.GetRelativePath(staged.Directory, file).Replace('\\', '/'))) throw new InvalidDataException("Unexpected file in staged installation.");
        if (expected.Count != 0) throw new InvalidDataException("Staged installation is incomplete.");
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<DirectoryInfo>(); pending.Push(new DirectoryInfo(root)); int entries = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                if (++entries > 40000 || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Unexpected link or excessive entries in staged installation.");
                if (item is DirectoryInfo child) pending.Push(child);
                else yield return item.FullName;
            }
        }
    }

    public static async Task<Process> StartVerifiedAsync(StagedUpdate staged, IReadOnlyList<string>? localArguments = null,
        CancellationToken cancellation = default)
    {
        // The public launch API always verifies. This fixed executable is the normal GUI, never a command from a feed.
        await VerifyStagedAsync(staged, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(staged.Executable) { UseShellExecute = false, WorkingDirectory = staged.Directory };
        foreach (string argument in localArguments ?? []) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("The updated editor could not be started.");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri url, string accept, CancellationToken cancellation)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            urls.Validate(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("SED-MotS-Updater/1.0");
            request.Headers.Accept.ParseAdd(accept);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new HttpRequestException("Update redirect has no location.");
                url = location.IsAbsoluteUri ? location : new Uri(url, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); urls.Validate(response.RequestMessage?.RequestUri ?? url); }
            catch { response.Dispose(); throw; }
            return response;
        }
        throw new HttpRequestException("Too many update redirects.");
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long maximum, CancellationToken cancellation)
    {
        byte[] buffer = new byte[65536]; long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false);
            if (read == 0) return total;
            total += read;
            if (total > maximum) throw new InvalidDataException("Download or archive entry exceeds its allowed size.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
        }
    }
}

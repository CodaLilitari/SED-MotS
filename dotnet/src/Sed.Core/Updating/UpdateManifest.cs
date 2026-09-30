using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sed.Core.Updating;

/// <summary>SED's released version family. The historic mots.N-dev suffix orders N numerically.</summary>
public sealed record SedReleaseVersion(int Major, int Minor, int Patch, int? Preview) : IComparable<SedReleaseVersion>
{
    public static SedReleaseVersion Parse(string text)
    {
        if (text.Length > 64) throw new InvalidDataException("Version is too long.");
        var m = Regex.Match(text, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-mots\.(0|[1-9][0-9]*)-dev)?\z", RegexOptions.CultureInvariant);
        if (!m.Success) throw new InvalidDataException("Unsupported SED release version.");
        int Number(int i) => int.TryParse(m.Groups[i].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n : throw new InvalidDataException("Version component is too large.");
        return new(Number(1), Number(2), Number(3), m.Groups[4].Success ? Number(4) : null);
    }
    public int CompareTo(SedReleaseVersion? other)
    {
        if (other is null) return 1;
        int c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        if (Preview is null) return other.Preview is null ? 0 : 1;
        return other.Preview is null ? -1 : Preview.Value.CompareTo(other.Preview.Value);
    }
}

public sealed record UpdateRelease(string Version, string Channel, string Platform, Uri PackageUrl,
    string Sha256, long SizeBytes, string EntryDirectory);

public static class UpdateManifest
{
    public const int MaxBytes = 256 * 1024;
    public const long MaxPackageBytes = 1024L * 1024 * 1024;

    public static IReadOnlyList<UpdateRelease> Parse(ReadOnlyMemory<byte> bytes, UpdateUrlPolicy urls)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Update manifest is too large.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        Fields(root, "schemaVersion", "releases");
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported update manifest schema.");
        var list = root.GetProperty("releases");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 100) throw new InvalidDataException("Invalid release list.");
        var result = new List<UpdateRelease>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            Fields(item, "version", "channel", "platform", "packageUrl", "sha256", "sizeBytes", "entryDirectory");
            string Text(string key, int limit = 128)
            {
                var value = item.GetProperty(key).GetString();
                return value is not null && value.Length <= limit ? value : throw new InvalidDataException($"Invalid {key}.");
            }
            string version = Text("version"); var parsed = SedReleaseVersion.Parse(version);
            string channel = Text("channel"), platform = Text("platform");
            if (channel is not ("stable" or "preview") || (channel == "stable") != (parsed.Preview is null))
                throw new InvalidDataException("Release channel and version disagree.");
            if (platform is not ("win-x64" or "linux-x64" or "osx-arm64")) throw new InvalidDataException("Unsupported release platform.");
            if (!identities.Add($"{platform}/{version}")) throw new InvalidDataException("Duplicate release identity, including same-version replacements, is forbidden.");
            if (!Uri.TryCreate(Text("packageUrl", 2048), UriKind.Absolute, out var url)) throw new InvalidDataException("Invalid package URL.");
            urls.Validate(url);
            string hash = Text("sha256");
            if (!Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid package SHA256.");
            long size = item.GetProperty("sizeBytes").GetInt64();
            if (size <= 0 || size > MaxPackageBytes) throw new InvalidDataException("Package size exceeds the supported limit.");
            string entry = Text("entryDirectory");
            if (entry.Length != 0 && !SafeUpdatePath.IsComponent(entry)) throw new InvalidDataException("Invalid package entry directory.");
            result.Add(new(version, channel, platform, url, hash.ToLowerInvariant(), size, entry));
        }
        return result;
    }

    public static UpdateRelease? Select(IReadOnlyList<UpdateRelease> releases, string currentVersion, string channel, string platform = "win-x64")
    {
        if (channel is not ("stable" or "preview")) throw new InvalidDataException("Unknown update channel.");
        var current = SedReleaseVersion.Parse(currentVersion);
        return releases.Where(r => r.Platform == platform && (channel == "preview" || r.Channel == "stable")
                && SedReleaseVersion.Parse(r.Version).CompareTo(current) > 0)
            .OrderByDescending(r => SedReleaseVersion.Parse(r.Version)).FirstOrDefault();
    }

    private static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a manifest object.");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate manifest property.");
        if (found.Count != names.Length) throw new InvalidDataException("Missing manifest property.");
    }
}

public sealed class UpdateUrlPolicy(IEnumerable<Uri> prefixes)
{
    private readonly Uri[] _prefixes = prefixes.ToArray();
    public void Validate(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0
            || url.Fragment.Length != 0 || url.Query.Length != 0 || url.OriginalString.Contains('%') || url.OriginalString.Contains('\\')
            || !_prefixes.Any(prefix => url.IdnHost.Equals(prefix.IdnHost, StringComparison.OrdinalIgnoreCase)
                && (prefix.AbsolutePath.EndsWith('/') ? url.AbsolutePath.StartsWith(prefix.AbsolutePath, StringComparison.Ordinal)
                    : url.AbsolutePath == prefix.AbsolutePath)))
            throw new InvalidDataException("Update URL is outside the trusted HTTPS project release paths.");
    }
}

internal static class SafeUpdatePath
{
    public static bool IsComponent(string value) => value.Length is > 0 and <= 180 && value is not ("." or "..")
        && !value.EndsWith('.') && !value.EndsWith(' ') && !value.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))
        && !Regex.IsMatch(value, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void RejectLinks(string path)
    {
        for (var info = new DirectoryInfo(Path.GetFullPath(path)); info is not null; info = info.Parent)
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update locations cannot contain symbolic links or junctions.");
    }
}

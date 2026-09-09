using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;

// Read only the manifest-selected package paths. Never extract archive entries,
// resolve their resource paths on disk, execute installers, or enumerate other corpora.
if (args.Length is < 2 or > 3) throw new ArgumentException("Usage: Sed.CommunityAudit <corpus-manifest.json> <report.json> [verified-wrapper-payload-manifest.json]");
const long maxPackageBytes = 512L * 1024 * 1024;
const long maxContainerBytes = 512L * 1024 * 1024;
const long maxJklBytes = 64L * 1024 * 1024;
const long maxExpandedBytesPerPackage = 1024L * 1024 * 1024;
const int maxArchiveEntries = 100_000;
string manifestPath = Path.GetFullPath(args[0]);
string corpusRoot = Path.GetDirectoryName(manifestPath)!;
string packageRoot = Path.GetFullPath(Path.Combine(corpusRoot, "packages")) + Path.DirectorySeparatorChar;
byte[] manifestBytes = File.ReadAllBytes(manifestPath);
byte[]? payloadManifestBytes = args.Length == 3 ? File.ReadAllBytes(args[2]) : null;
using var manifest = JsonDocument.Parse(manifestBytes);
using var payloadManifest = payloadManifestBytes is not null ? JsonDocument.Parse(payloadManifestBytes) : null;
var packageResults = new List<object>();
var levelResults = new List<object>();
var seenIds = new HashSet<int>();
var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
int verifiedPackages = 0, packageFailures = 0, containerFailures = 0, jklDiscovered = 0,
    parsedDocuments = 0, geometryDocuments = 0, parseFailures = 0, noEditPasses = 0,
    regenerationPasses = 0, roundtripFailures = 0, boundedSkips = 0, packagesWithoutJkl = 0;

foreach (var item in manifest.RootElement.GetProperty("packages").EnumerateArray())
{
    int id = item.GetProperty("fileId").GetInt32();
    string category = item.GetProperty("category").GetString()!;
    string title = item.GetProperty("title").GetString()!;
    var gaps = new List<string>();
    var containers = new List<object>();
    var wrappers = new List<string>();
    int packageDocuments = 0;
    long expandedBytes = 0;
    string? packageError = null, actualHash = null;
    bool verified = false;
    try
    {
        if (!seenIds.Add(id)) throw new InvalidDataException("Duplicate package ID in manifest.");
        if (category is not ("jksp" or "jkmp" or "motssp" or "motsmp"))
            throw new InvalidDataException("Unknown original-Massassi category.");
        string path = Path.GetFullPath(item.GetProperty("localPath").GetString()!);
        string expectedPath = Path.GetFullPath(Path.Combine(packageRoot, category, id + ".zip"));
        if (!path.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) ||
            !path.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) || !seenPaths.Add(path))
            throw new InvalidDataException("Package path is not the unique manifest category/file-ID ZIP under this corpus.");
        for (string? parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= corpusRoot.Length; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Corpus path contains a reparse point.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package is a reparse point.");
        using var stream = File.OpenRead(path);
        if (stream.Length > maxPackageBytes || stream.Length != item.GetProperty("bytes").GetInt64())
            throw new InvalidDataException("Package size does not match acquisition manifest or exceeds audit bound.");
        actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!actualHash.Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package SHA256 differs from acquisition manifest.");
        verified = true;
        verifiedPackages++;
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > maxArchiveEntries) throw new InvalidDataException("ZIP entry count exceeds audit bound.");
        foreach (var entry in zip.Entries)
        {
            string ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
            if (ext is ".exe" or ".msi" or ".zip" or ".7z" or ".rar") wrappers.Add(entry.FullName);
            if (ext is not (".jkl" or ".gob" or ".goo")) continue;
            long bound = ext == ".jkl" ? maxJklBytes : maxContainerBytes;
            if (entry.Length > bound || expandedBytes + entry.Length > maxExpandedBytesPerPackage)
            {
                boundedSkips++;
                gaps.Add($"Bounded skip: {entry.FullName} ({entry.Length} bytes).");
                continue;
            }
            using var data = ReadEntry(entry, bound);
            expandedBytes += data.Length;
            if (ext == ".jkl")
            {
                AuditJkl(data.ToArray(), entry.FullName);
                continue;
            }
            AuditGob(data, entry.FullName);
        }
        if (payloadManifest is not null)
        {
            foreach (var payload in payloadManifest.RootElement.GetProperty("payloads").EnumerateArray())
            {
                if (payload.GetProperty("fileId").GetInt32() != id) continue;
                if (payload.GetProperty("sourceZipSha256").GetString() != actualHash ||
                    payload.GetProperty("category").GetString() != category)
                    throw new InvalidDataException("Supplemental payload parent does not match original ZIP provenance.");
                string payloadPath = Path.GetFullPath(payload.GetProperty("localPath").GetString()!);
                string allowedRoot = Path.GetFullPath(Path.Combine(corpusRoot, "working", "installer-inspection", category, id.ToString())) + Path.DirectorySeparatorChar;
                if (!payloadPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) ||
                    !(Path.GetDirectoryName(payloadPath) + Path.DirectorySeparatorChar).Equals(allowedRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Supplemental payload must be a direct generated file in its original package's inspection directory.");
                for (string? current = payloadPath; current is not null && current.Length >= corpusRoot.Length; current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Supplemental payload path contains a reparse point.");
                string payloadExt = Path.GetExtension(payloadPath).ToLowerInvariant();
                if (payloadExt is not (".gob" or ".goo" or ".jkl")) { gaps.Add("Supplemental format not audited: " + payloadExt); continue; }
                using var payloadStream = File.OpenRead(payloadPath);
                long payloadBound = payloadExt == ".jkl" ? maxJklBytes : maxExpandedBytesPerPackage;
                if (payloadStream.Length != payload.GetProperty("bytes").GetInt64() || payloadStream.Length > payloadBound ||
                    expandedBytes + payloadStream.Length > maxExpandedBytesPerPackage)
                    throw new InvalidDataException("Supplemental payload size mismatch or audit bound exceeded.");
                string payloadHash = Convert.ToHexString(SHA256.HashData(payloadStream)).ToLowerInvariant();
                if (payloadHash != payload.GetProperty("sha256").GetString()) throw new InvalidDataException("Supplemental payload SHA256 mismatch.");
                payloadStream.Position = 0;
                expandedBytes += payloadStream.Length;
                string sourceEntry = payload.GetProperty("wrapperEntry").GetString() + "!/" + payload.GetProperty("entry").GetString();
                if (payloadExt == ".jkl")
                {
                    using var bytes = new MemoryStream();
                    payloadStream.CopyTo(bytes);
                    AuditJkl(bytes.ToArray(), sourceEntry);
                }
                else AuditGob(payloadStream, sourceEntry);
            }
        }
        if (wrappers.Count > 0) gaps.Add("Installer/nested archives require separate coverage. Only hash-verified listed supplemental payloads, if supplied, were inspected; no installer was executed.");
        if (packageDocuments == 0) { packagesWithoutJkl++; gaps.Add("No directly accessible JKL found in the ZIP, its direct GOB/GOO entries, or supplied verified wrapper payloads."); }
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        packageFailures++;
        packageError = error.GetType().Name + ": " + error.Message;
    }
    packageResults.Add(new { fileId = id, category, title, sourceUrl = item.GetProperty("sourceUrl").GetString(),
        integrityVerified = verified, sha256 = actualHash, discoveredJklDocuments = packageDocuments,
        expandedBytesRead = expandedBytes, containers, wrapperEntries = wrappers, capabilityGaps = gaps, error = packageError });
    Console.WriteLine($"{category}/{id}: JKL={packageDocuments}, integrity={verified}, error={packageError ?? "none"}");

    void AuditGob(Stream data, string name)
    {
        int nestedCount = 0;
        string? containerError = null;
        string[] nativeProjects = [];
        try
        {
            using var gob = new GobArchive(data);
            if (gob.Entries.Count > maxArchiveEntries) throw new InvalidDataException("GOB entry count exceeds audit bound.");
            nativeProjects = gob.FindByExtension(".jed").Select(e => e.NormalizedName).ToArray();
            foreach (var nested in gob.FindByExtension(".jkl"))
            {
                nestedCount++;
                if (nested.Length > maxJklBytes || expandedBytes + nested.Length > maxExpandedBytesPerPackage)
                {
                    boundedSkips++;
                    gaps.Add($"Bounded skip: {name}!/{nested.NormalizedName} ({nested.Length} bytes).");
                    continue;
                }
                expandedBytes += nested.Length;
                AuditJkl(gob.ReadBytes(nested), name + "!/" + nested.NormalizedName);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            containerFailures++;
            containerError = error.GetType().Name + ": " + error.Message;
        }
        containers.Add(new { entry = name, bytes = data.Length, jklEntries = nestedCount, nativeProjectEntries = nativeProjects, error = containerError });
    }

    void AuditJkl(byte[] sourceBytes, string entryName)
    {
        jklDiscovered++; packageDocuments++;
        string sourceHash = Hash(sourceBytes);
        string source = Encoding.Latin1.GetString(sourceBytes);
        string? beforeDigest = null, afterDigest = null, difference = null, error = null;
        bool? exact = null, modeled = null, materials = null;
        int? sectors = null, things = null, architectureVertices = null;
        string? kind = null;
        string[] warnings = [];
        string phase = "parse";
        try
        {
            var doc = JklParser.ParseDocument(source);
            byte[] originalModeledData = Serialize(doc.Level);
            beforeDigest = Hash(originalModeledData);
            parsedDocuments++;
            sectors = doc.Level.Sectors.Count;
            if (sectors > 0) geometryDocuments++;
            things = doc.Level.Things.Count;
            kind = doc.Level.Kind.ToString();
            architectureVertices = doc.Level.ArchitecturalLighting.Sum(o => o.Meshes.Sum(m => m.Vertices.Count));
            phase = "no_edit_write";
            exact = JklWriter.Build(doc) == Normalize(source);
            if (exact == true) noEditPasses++;
            warnings = doc.PreservationWarnings.ToArray();
            phase = "forced_regeneration";
            var regenerated = new JklDocument(doc.Level);
            regenerated.Materials.AddRange(doc.Materials);
            string generated = JklWriter.Build(regenerated);
            phase = "reparse_regenerated";
            var back = JklParser.ParseDocument(generated);
            afterDigest = Digest(back.Level);
            materials = doc.Materials.SequenceEqual(back.Materials);
            modeled = beforeDigest == afterDigest && materials == true;
            if (modeled == true) regenerationPasses++;
            else
            {
                using var beforeJson = JsonDocument.Parse(originalModeledData);
                using var afterJson = JsonDocument.Parse(Serialize(back.Level));
                difference = FirstDifference(beforeJson.RootElement, afterJson.RootElement, "$model")
                    ?? (materials == false ? "$document.materials" : "$model.serialization");
            }
            if (exact != true || modeled != true) roundtripFailures++;
            phase = "complete";
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            if (phase == "parse") parseFailures++; else roundtripFailures++;
            error = failure.GetType().Name + ": " + failure.Message;
        }
        levelResults.Add(new { fileId = id, category, entry = entryName, sourceBytes = sourceBytes.Length,
            sourceSha256 = sourceHash, phase, parsedKind = kind, sectors, things, architectureVertices,
            noEditTextPreserved = exact, modeledRegenerationPreserved = modeled, materialTablePreserved = materials,
            beforeDigest, afterDigest, firstModeledDifference = difference, preservationWarnings = warnings, error });
    }
}
var report = new
{
    schemaVersion = 1, recordedAtUtc = DateTime.UtcNow,
    sourceManifest = manifestPath, sourceManifestSha256 = Hash(manifestBytes),
    supplementalPayloadManifest = args.Length == 3 ? Path.GetFullPath(args[2]) : null,
    supplementalPayloadManifestSha256 = payloadManifestBytes is not null ? Hash(payloadManifestBytes) : null,
    expectedPackages = manifest.RootElement.GetProperty("totalSelected").GetInt32(),
    inspectedPackages = packageResults.Count, verifiedPackages, packageFailures, containerFailures,
    jklDiscovered, parsedDocuments, geometryDocuments, parseFailures, noEditPasses, regenerationPasses,
    roundtripFailures, boundedSkips, packagesWithoutJkl,
    bounds = new { maxPackageBytes, maxContainerBytes, maxJklBytes, maxExpandedBytesPerPackage, maxArchiveEntries },
    formatAssemblySha256 = Hash(File.ReadAllBytes(typeof(JklParser).Assembly.Location)),
    verifierAssemblySha256 = Hash(File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location)),
    scope = "Manifest-selected unchanged ZIPs; loose JKL, direct GOB/GOO, and optional hash-verified passively recovered wrapper payloads. Exact comparison normalizes line endings. Forced regeneration compares explicit modeled data/material tables. Unrecovered wrapper contents, unknown fields/sections, assets, UI, game load and rendering are not certified.",
    downloadedPayloadsExecuted = false, extractedFilesByThisVerifier = false,
    suppliedPassivelyExtractedPayloads = payloadManifest is not null, includedThirdPartyAssets = false,
    packages = packageResults, documents = levelResults
};
string outputPath = Path.GetFullPath(args[1]);
if (outputPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase) || outputPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase) ||
    (args.Length == 3 && outputPath.Equals(Path.GetFullPath(args[2]), StringComparison.OrdinalIgnoreCase)))
    throw new ArgumentException("Audit output must not overwrite manifest or packages.");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Packages={verifiedPackages}/{packageResults.Count}; JKL={jklDiscovered}; parsed={parsedDocuments}; geometry={geometryDocuments}; no-edit={noEditPasses}; regenerated={regenerationPasses}; parse failures={parseFailures}; roundtrip failures={roundtripFailures}; container failures={containerFailures}; wrapper/no-JKL packages={packagesWithoutJkl}");
return packageFailures + containerFailures + parseFailures + roundtripFailures == 0 && boundedSkips == 0 && jklDiscovered > 0 ? 0 : 1;

static MemoryStream ReadEntry(ZipArchiveEntry entry, long bound)
{
    if (entry.Length < 0 || entry.Length > bound || entry.Length > int.MaxValue) throw new InvalidDataException("Entry exceeds audit bound.");
    var data = new MemoryStream((int)entry.Length);
    try
    {
        using var input = entry.Open();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            if (data.Length + read > bound || data.Length + read > entry.Length) throw new InvalidDataException("Expanded entry exceeds declared size or audit bound.");
            data.Write(buffer, 0, read);
        }
        if (data.Length != entry.Length) throw new InvalidDataException("ZIP entry length mismatch.");
        data.Position = 0;
        return data;
    }
    catch { data.Dispose(); throw; }
}
static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static string Digest(Level level) => Hash(Serialize(level));
static byte[] Serialize(Level level)
{
    // Entity IDs and pooled world-vertex indices are editor bookkeeping.
    // Position/topology order, component values, flags and bindings are semantic.
    var surfaces = level.Sectors.SelectMany(s => s.Surfaces).ToList();
    var data = new
    {
        level.Kind,
        header = new { level.Header.Version, level.Header.Gravity, level.Header.CeilingSky,
            level.Header.HorizonSky, level.Header.MipmapDistances, level.Header.LodDistances,
            level.Header.PerspectiveDistance, level.Header.GouraudDistance },
        sectors = level.Sectors.Select(s => new { s.Flags, s.Ambient, s.ExtraLight, s.Tint, s.ColorMap,
            s.Sound, s.SoundVolume, s.Thrust, s.Layer, s.HasCollideBox, s.BoundBox, s.Center, s.Radius,
            collide = s.HasCollideBox ? new { s.CollideBox.Min, s.CollideBox.Max } : null,
            positions = s.Vertices.Select(v => v.Position),
            surfaces = s.Surfaces.Select(f => new { f.Material, f.MaterialIndex, f.SurfFlags, f.FaceFlags,
                f.Geo, f.Light, f.Tex, f.ExtraLightIntensity, f.AdjoinFlags,
                adjoin = f.Adjoin is null ? -1 : surfaces.IndexOf(f.Adjoin),
                corners = f.Corners.Select(c => new { c.Vertex.Position, c.Uv, c.Intensity,
                    mono = c.MonoIntensity ?? c.Intensity.R }) }) }),
        things = level.Things.Select(t => new { t.Name, t.Template, t.Position, t.Pitch, t.Yaw, t.Roll,
            sector = t.Sector?.Num ?? -1, t.Layer, t.ArchLightingIndex, t.Values, t.SourceParameters }),
        level.ThingCapacity, level.ArchitecturalLighting, level.Layers, level.ColorMaps,
        lights = level.Lights.Select(l => new { l.Flags, l.Layer, l.Color, l.Intensity, l.Range, l.Position }),
        cogs = level.Cogs.Select(c => new { c.Name, c.Values }),
        templates = level.Templates.Values.Select(t => new { t.Name, t.Parent, t.Values, t.SourceParameters })
    };
    return JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { IncludeFields = true });
}

static string? FirstDifference(JsonElement before, JsonElement after, string path)
{
    if (before.ValueKind != after.ValueKind) return path + ".kind";
    if (before.ValueKind == JsonValueKind.Object)
    {
        if (before.EnumerateObject().Count() != after.EnumerateObject().Count()) return path + ".propertyCount";
        foreach (var property in before.EnumerateObject())
        {
            if (!after.TryGetProperty(property.Name, out var value)) return path + "." + property.Name;
            string? difference = FirstDifference(property.Value, value, path + "." + property.Name);
            if (difference is not null) return difference;
        }
        return null;
    }
    if (before.ValueKind == JsonValueKind.Array)
    {
        if (before.GetArrayLength() != after.GetArrayLength()) return path + ".length";
        for (int i = 0; i < before.GetArrayLength(); i++)
        {
            string? difference = FirstDifference(before[i], after[i], path + $"[{i}]");
            if (difference is not null) return difference;
        }
        return null;
    }
    return before.GetRawText() == after.GetRawText() ? null : path;
}


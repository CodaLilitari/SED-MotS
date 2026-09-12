using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Gob;
using Sed.Formats.Jkl;

if (args.Length != 2) throw new ArgumentException("Usage: Sed.MotsFormatAudit <licensed-MotS-root> <report.json>");
var results = new List<object>();
var archiveHashes = new Dictionary<string, string>();
int total = 0, failures = 0, bindings = 0, vertices = 0;
foreach (string archivePath in Directory.GetFiles(Path.Combine(args[0], "episode"), "*.goo").Order())
{
    using (var archiveFile = File.OpenRead(archivePath))
        archiveHashes.Add(Path.GetFileName(archivePath), Convert.ToHexString(SHA256.HashData(archiveFile)).ToLowerInvariant());
    using var archive = GobArchive.Open(archivePath);
    foreach (var entry in archive.FindByExtension(".jkl"))
    {
        string source = archive.ReadText(entry);
        try
        {
            var doc = JklParser.ParseDocument(source);
            if (doc.Level.Sectors.Count == 0) continue;
            total++;
            string unchanged = JklWriter.Build(doc);
            bool exact = unchanged == Normalize(source);
            // Force modeled sections through serialization too: equality of raw
            // sections alone cannot prove the independent mono/RGB model works.
            var regenerated = new JklDocument(doc.Level);
            regenerated.Materials.AddRange(doc.Materials);
            var back = JklParser.ParseDocument(JklWriter.Build(regenerated));
            string beforeDigest = Digest(doc.Level);
            string afterDigest = Digest(back.Level);
            bool modeled = beforeDigest == afterDigest;
            string? difference = modeled ? null : FirstDifference(JsonDocument.Parse(Serialize(doc.Level)).RootElement,
                JsonDocument.Parse(Serialize(back.Level)).RootElement, "$model");
            if (!exact || !modeled) failures++;
            int count = doc.Level.Things.Count(t => t.ArchLightingIndex >= 0);
            int vertexCount = doc.Level.ArchitecturalLighting.Sum(o => o.Meshes.Sum(m => m.Vertices.Count));
            bindings += count;
            vertices += vertexCount;
            results.Add(new { archive = Path.GetFileName(archivePath), entry = entry.NormalizedName,
                sourceSha256 = Hash(Encoding.Latin1.GetBytes(source)), noEditTextPreserved = exact,
                modeledRegenerationPreserved = modeled, beforeDigest, afterDigest,
                firstModeledDifference = difference,
                architectureBindings = count, architectureVertices = vertexCount,
                repeatedThingParameterRows = doc.Level.Things.Count(t => t.SourceParameters.GroupBy(p => p.Key).Any(g => g.Count() > 1)),
                repeatedTemplateParameterRows = doc.Level.Templates.Values.Count(t => t.SourceParameters.GroupBy(p => p.Key).Any(g => g.Count() > 1)),
                unmodeledFieldWarnings = doc.PreservationWarnings.Count });
            Console.WriteLine($"{entry.NormalizedName}: no-edit={exact}, modeled={modeled}, bindings={count}{(difference is null ? "" : ", difference=" + difference)}");
        }
        catch (Exception error)
        {
            failures++;
            results.Add(new { archive = Path.GetFileName(archivePath), entry = entry.NormalizedName,
                error = error.Message, sourceSha256 = Hash(Encoding.Latin1.GetBytes(source)) });
            Console.WriteLine($"{entry.NormalizedName}: FAIL {error.Message}");
        }
    }
}
var report = new { recordedAtUtc = DateTime.UtcNow, playableLevels = total, failures,
    architectureBindings = bindings, architectureVertices = vertices,
    archiveSha256 = archiveHashes,
    auditAssemblySha256 = Hash(File.ReadAllBytes(typeof(JklParser).Assembly.Location)),
    verifierAssemblySha256 = Hash(File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location)),
    scope = "Read-only native .NET full JKL parse/no-edit text preservation and explicitly modeled semantic regeneration. No editor UI, engine load, rendering, external asset validation, or complete unknown-field regeneration claim.",
    licensedPayloadsIncluded = false, levels = results };
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Levels={total}; failures={failures}; bindings={bindings}; architecture vertices={vertices}");
return failures == 0 && total > 0 ? 0 : 1;

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
        lights = level.Lights.Select(l => new { l.Flags, l.Layer, l.Color, l.Intensity, l.Range, l.Position,
            l.RgbIntensity, l.LegacyRgbRange }),
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

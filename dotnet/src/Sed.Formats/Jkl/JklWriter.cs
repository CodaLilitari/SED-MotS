using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sed.Core.Model;

namespace Sed.Formats.Jkl;

/// <summary>
/// Writes an edited <see cref="JklDocument"/> back to disk. The GEORESOURCE,
/// SECTORS, THINGS, LIGHTS, COGS, TEMPLATES, HEADER, and LAYERS sections are
/// regenerated from the model; any unrecognized section is kept verbatim.
/// </summary>
public static class JklWriter
{
    public static void Save(JklDocument doc, string path)
    {
        string content = BuildCandidate(doc, out var materials);
        string fullPath = Path.GetFullPath(path);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, fullPath, overwrite: true);
            doc.Materials.AddRange(materials.Skip(doc.Materials.Count));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Generates output without changing the document or derived geometry caches.</summary>
    public static string Build(JklDocument doc) => BuildCandidate(doc, out _);

    private static string BuildCandidate(JklDocument doc, out List<string> materials)
    {
        var src = doc.SourceLines;
        SourceLightMetadataFormat.ValidateRetainedSource(doc);
        if (src.Length > 0 && (doc.OriginalSourceTextHash is null || doc.SourceGeometry is null))
            throw new InvalidOperationException("Export requires a parsed source baseline, including validated source-light metadata when present. Reopen the source through JklParser.ParseDocument before saving.");
        JklSourceGeometry.ValidateRetainedSource(doc);
        if (doc.OriginalSourceTextHash is not null && doc.OriginalSourceTextHash != SourceTextHash(src))
            throw new InvalidOperationException("Retained source text changed outside the document model. Reopen the changed source through JklParser.ParseDocument before exporting it.");
        var level = doc.Level;
        var ranges = new List<(int start, int end, List<string> replacement)>();
        var appended = new List<List<string>>();
        var sections = GenerateSections(doc, out materials, enforceGeometryIdentity: true);
        foreach (var section in sections)
            AddSection(ranges, appended, doc, section.name, section.lines, section.hasContent);
        ranges.Sort((a, b) => a.start != b.start ? a.start.CompareTo(b.start) : a.end.CompareTo(b.end));

        var result = new List<string>(src.Length + 64);
        int i = 0, r = 0;
        while (i < src.Length)
        {
            if (r < ranges.Count && i == ranges[r].start)
            {
                result.AddRange(ranges[r].replacement);
                i = ranges[r].end + 1;
                r++;
                continue;
            }
            result.Add(src[i]);
            i++;
        }

        // Sections the source never had. LIGHTS and LAYERS are editor-authored and
        // absent from every retail level, so without this a light placed in the
        // editor would vanish on save.
        foreach (var section in appended)
        {
            result.Add(string.Empty);
            result.AddRange(section);
        }

        return string.Join('\n', result);
    }

    internal static void CaptureBaseline(JklDocument doc)
    {
        doc.OriginalSourceLightTextHash = SourceLightMetadataFormat.SourceTextHash(doc.SourceLines);
        bool promoteSourceLights = SourceLightMetadataFormat.Needed(doc.Level) &&
            FindSection(doc.SourceLines, SourceLightMetadataFormat.SectionName).start < 0;
        var sections = GenerateSections(doc, out var materials, captureBaseline: true);
        foreach (var section in sections)
        {
            // A legacy JK LIGHTS row may contain inactive RGB color. Once it is
            // represented in metadata, rewrite its ordinary row and metadata
            // together so the checksum describes the actual serialized base.
            if (promoteSourceLights && section.name is "LIGHTS" or SourceLightMetadataFormat.SectionName) continue;
            doc.OriginalSections.Add(section.name, string.Join('\n', section.lines));
        }
        doc.Materials.AddRange(materials.Skip(doc.Materials.Count));
        doc.OriginalSourceTextHash = SourceTextHash(doc.SourceLines);
    }

    private static string SourceTextHash(string[] lines) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

    private static List<(string name, List<string> lines, bool hasContent)> GenerateSections(JklDocument doc,
        out List<string> materials, bool enforceGeometryIdentity = false, bool captureBaseline = false)
    {
        var level = doc.Level;
        materials = doc.Materials.ToList();
        foreach (var surface in level.Sectors.SelectMany(s => s.Surfaces))
            if (!string.IsNullOrWhiteSpace(surface.Material) &&
                !materials.Contains(surface.Material, StringComparer.OrdinalIgnoreCase))
                materials.Add(surface.Material);
        var (geo, sectors, identityError) = GeoResourceWriter.Build(level, materials, doc.SourceGeometry);
        if (captureBaseline) doc.OriginalGeometryIdentityError = identityError;
        if (enforceGeometryIdentity && identityError is not null &&
            (doc.OriginalGeometryIdentityError is null || Changed("GEORESOURCE", geo) || Changed("SECTORS", sectors)))
            throw new InvalidOperationException("Cannot rewrite geometry while preserving imported index references: " + identityError +
                " No COG or opaque reference values were inferred or remapped.");
        bool Changed(string name, List<string> lines) =>
            !doc.OriginalSections.TryGetValue(name, out string? original) || original != string.Join('\n', lines);
        if (enforceGeometryIdentity && (Changed("GEORESOURCE", geo) || Changed("SECTORS", sectors)))
        {
            JklSourceGeometry.ValidateRetainedSource(doc);
            JklGeometrySectionPolicy.EnsureSupported(doc);
        }
        var arch = new List<string> { "SECTION: ARCHLIGHTING" };
        arch.AddRange(ArchitecturalLightingFormat.Build(level.ArchitecturalLighting));
        arch.Add("END");
        return new()
        {
            ("HEADER", GenerateHeader(level), true), ("MATERIALS", GenerateMaterials(doc, materials), materials.Count > 0),
            ("GEORESOURCE", geo, true), ("SECTORS", sectors, true),
            ("TEMPLATES", GenerateTemplates(level), level.Templates.Count > 0),
            ("THINGS", GenerateThings(level), level.Things.Count > 0),
            ("LIGHTS", GenerateLights(level), level.Lights.Count > 0),
            (SourceLightMetadataFormat.SectionName, SourceLightMetadataFormat.Build(level), SourceLightMetadataFormat.Needed(level)),
            ("COGS", GenerateCogs(level), level.Cogs.Count > 0),
            ("LAYERS", GenerateLayers(level), level.Layers.Count > 0),
            ("ARCHLIGHTING", arch, level.Kind == ProjectType.MysteriesOfTheSith)
        };
    }

    private static List<string> GenerateMaterials(JklDocument doc, IReadOnlyList<string> materials)
    {
        var originalRows = new Dictionary<int, string>();
        var (start, end) = FindSection(doc.SourceLines, "MATERIALS");
        for (int i = start + 1; start >= 0 && i <= end; i++)
        {
            string line = doc.SourceLines[i];
            var first = JklReader.FirstWord(line.Trim());
            if (first.EndsWith(':') && int.TryParse(first[..^1], out int index)) originalRows[index] = line;
        }
        var lines = new List<string> { "SECTION: MATERIALS", $"World materials {materials.Count}" };
        for (int i = 0; i < materials.Count; i++)
        {
            if (originalRows.TryGetValue(i, out string? row) &&
                JklReader.WordAt(JklReader.StripIndex(row), 0).Equals(materials[i], StringComparison.OrdinalIgnoreCase))
                lines.Add(row);
            else lines.Add($"{i}: {materials[i]} 1 1");
        }
        lines.Add("END");
        return lines;
    }

    /// <summary>
    /// Registers a section for rewriting. When the source already has the section
    /// its lines are replaced in place; when it does not and
    /// <paramref name="hasContent"/> is true, the generated section is appended at
    /// the end of the file instead of being dropped. Sections with no content are
    /// skipped entirely so untouched levels do not sprout empty ones.
    /// </summary>
    private static void AddSection(List<(int, int, List<string>)> ranges, List<List<string>> appended,
        JklDocument doc, string name, List<string> replacement, bool hasContent)
    {
        var lines = doc.SourceLines;
        if (doc.OriginalSections.TryGetValue(name, out string? original) &&
            original == string.Join('\n', replacement)) return;
        if (doc.SectionsWithUnmodeledFields.Contains(name))
            throw new InvalidOperationException($"Cannot rewrite {name}: its source contains unmodeled fields. Keep the original section or use an editor that supports those fields.");
        if (doc.Level.Kind == ProjectType.InfernalMachine && name is "GEORESOURCE" or "SECTORS")
            throw new NotSupportedException("Regenerating Infernal Machine geometry is not supported; unchanged source can be retained.");
        var (start, end) = FindSection(lines, name);
        if (start >= 0) ranges.Add((start, end, replacement));
        else if (hasContent && name == "MATERIALS" && FindSection(lines, "GEORESOURCE").start is var geoStart && geoStart >= 0)
            ranges.Add((geoStart, geoStart - 1, replacement));
        else if (hasContent) appended.Add(replacement);
    }

    /// <summary>
    /// Finds a "SECTION: name" line and the line that closes it: the section's own
    /// "END" (inclusive) or the line before the next "SECTION:" (exclusive) — JK
    /// sections don't all use an explicit END.
    /// </summary>
    private static (int start, int end) FindSection(string[] lines, string name)
    {
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Split('#')[0].Trim();
            if (start < 0)
            {
                if (IsSectionHeader(t, out var hdr) && hdr.Equals(name, StringComparison.OrdinalIgnoreCase))
                    start = i;
            }
            else if (t.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                return (start, i);            // explicit END (inclusive)
            }
            else if (IsSectionHeader(t, out _))
            {
                return (start, i - 1);        // next section begins (exclusive)
            }
        }
        return (start, start >= 0 ? lines.Length - 1 : -1);
    }

    private static bool IsSectionHeader(string trimmed, out string name)
    {
        name = string.Empty;
        if (!trimmed.StartsWith("SECTION:", StringComparison.OrdinalIgnoreCase)) return false;
        name = trimmed[8..].Trim();
        return true;
    }

    private static List<string> GenerateThings(Level level)
    {
        var lines = new List<string> { "SECTION: THINGS", "", $"World things {System.Math.Max(level.ThingCapacity, level.Things.Count)}" };
        for (int i = 0; i < level.Things.Count; i++)
        {
            var t = level.Things[i];
            var template = t.Template.Length > 0 ? t.Template : "none";
            var name = t.Name.Length > 0 ? t.Name : "thing";
            int sector = t.Sector is null ? -1 : level.Sectors.IndexOf(t.Sector);
            if (t.Sector is not null && sector < 0) throw new InvalidOperationException($"Thing {i} references a deleted sector.");
            var line = $"{i}: {template} {name} {F(t.Position.X)} {F(t.Position.Y)} {F(t.Position.Z)} " +
                       $"{F(t.Pitch)} {F(t.Yaw)} {F(t.Roll)} {sector}";
            if (level.Kind == ProjectType.MysteriesOfTheSith)
            {
                if (t.ArchLightingIndex < -1 || t.ArchLightingIndex >= level.ArchitecturalLighting.Count)
                    throw new InvalidOperationException($"Thing {i} references missing architectural lighting object {t.ArchLightingIndex}.");
                if (t.ArchLightingIndex >= 0 && !string.Equals(t.ArchLightingSourceModel,
                    level.GetThingModel(t), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Thing {i} model changed after architectural lighting import; unbind or rebake before saving.");
                line += $" {t.ArchLightingIndex}";
            }
            foreach (var (k, v) in ParameterRecords.Merge(t.SourceParameters, t.Values)) line += $" {k}={v}";
            lines.Add(line);
        }
        lines.Add("end");
        lines.Add("");
        return lines;
    }

    private static List<string> GenerateHeader(Level level)
    {
        var h = level.Header;
        var ijim = level.Kind == ProjectType.InfernalMachine;
        var lines = new List<string> { "SECTION: HEADER" };
        lines.Add($"VERSION {h.Version}");
        lines.Add($"WORLD GRAVITY {F8(h.Gravity)}");
        lines.Add($"CEILING SKY Z {F8(h.CeilingSky.Height)}");
        lines.Add($"HORIZON DISTANCE {F8(h.HorizonSky.Distance)}");
        lines.Add($"HORIZON PIXELS PER REV {F6(h.HorizonSky.PixelsPerRev)}");
        lines.Add($"HORIZON SKY OFFSET {F8(h.HorizonSky.Offset.X)} {F8(h.HorizonSky.Offset.Y)}");
        lines.Add($"CEILING SKY OFFSET {F8(h.CeilingSky.Offset.X)} {F8(h.CeilingSky.Offset.Y)}");
        if (!ijim)
            lines.Add($"MIPMAP DISTANCES {F6(h.MipmapDistances[0])} {F6(h.MipmapDistances[1])} {F6(h.MipmapDistances[2])} {F6(h.MipmapDistances[3])}");
        lines.Add($"LOD DISTANCES {F6(h.LodDistances[0])} {F6(h.LodDistances[1])} {F6(h.LodDistances[2])} {F6(h.LodDistances[3])}");
        if (ijim)
            lines.Add($"FOG {(h.Fog.Enabled ? 1 : 0)} {F8(h.Fog.Color.R)} {F8(h.Fog.Color.G)} {F8(h.Fog.Color.B)} {F8(1.0)} {F8(h.Fog.Start)} {F8(h.Fog.End)}");
        if (!ijim)
            lines.Add($"PERSPECTIVE DISTANCE {F6(h.PerspectiveDistance)}");
        if (!ijim)
            lines.Add($"GOURAUD DISTANCE {F6(h.GouraudDistance)}");
        lines.Add("END");
        lines.Add("");
        return lines;
    }

    private static List<string> GenerateTemplates(Level level)
    {
        var lines = new List<string> { "SECTION: TEMPLATES", "", $"World templates {level.Templates.Count}" };
        foreach (var tpl in level.Templates.Values.OrderBy(t => t.Order))
        {
            // The parent occupies a fixed second token, so an empty one must be
            // written as "none" (the same sentinel THINGS uses). Emitting nothing
            // would shift the first parameter into the parent slot and lose it.
            var parent = string.IsNullOrWhiteSpace(tpl.Parent) ? "none" : tpl.Parent;

            var sb = new StringBuilder();
            sb.Append(tpl.Name).Append(' ').Append(parent);
            foreach (var (k, v) in ParameterRecords.Merge(tpl.SourceParameters, tpl.Values))
                sb.Append(' ').Append(k).Append('=').Append(v);
            lines.Add(sb.ToString());
        }
        lines.Add("end");
        lines.Add("");
        return lines;
    }

    private static List<string> GenerateLights(Level level)
    {
        var motsOrIjim = level.Kind is ProjectType.MysteriesOfTheSith or ProjectType.InfernalMachine;
        var lines = new List<string> { "SECTION: LIGHTS", "", $"Editor lights {level.Lights.Count}" };
        for (int i = 0; i < level.Lights.Count; i++)
        {
            var lt = level.Lights[i];
            var line = $"{i}: 0x{lt.Flags:x} {lt.Layer} {F8(lt.Position.X)} {F8(lt.Position.Y)} {F8(lt.Position.Z)} {F8(lt.Range)} {F8(lt.Intensity)}";
            if (motsOrIjim)
                line += $" {F8(lt.Color.R)} {F8(lt.Color.G)} {F8(lt.Color.B)}";
            lines.Add(line);
        }
        lines.Add("end");
        lines.Add("");
        return lines;
    }

    private static List<string> GenerateCogs(Level level)
    {
        var lines = new List<string> { "SECTION: COGS", "", $"World cogs\t{level.Cogs.Count}" };
        for (int i = 0; i < level.Cogs.Count; i++)
        {
            var cog = level.Cogs[i];
            var sb = new StringBuilder();
            sb.Append(i).Append(":\t").Append(cog.Name);
            foreach (var v in cog.Values) sb.Append('\t').Append(v);
            lines.Add(sb.ToString());
        }
        lines.Add("end");
        lines.Add("");
        return lines;
    }

    private static List<string> GenerateLayers(Level level)
    {
        var lines = new List<string> { "SECTION: LAYERS", "", $"Editor layers {level.Layers.Count}" };

        for (int li = 0; li < level.Layers.Count; li++)
        {
            lines.Add($"#{level.Layers[li]}");

            var secs = new List<int>();
            for (int si = 0; si < level.Sectors.Count; si++)
                if (level.Sectors[si].Layer == li) secs.Add(si);

            var sbSec = new StringBuilder().Append(secs.Count).Append(':');
            foreach (var s in secs) sbSec.Append('\t').Append(s);
            lines.Add(sbSec.ToString());

            var ths = new List<int>();
            for (int ti = 0; ti < level.Things.Count; ti++)
                if (level.Things[ti].Layer == li) ths.Add(ti);

            var sbTh = new StringBuilder().Append(ths.Count).Append(':');
            foreach (var t in ths) sbTh.Append('\t').Append(t);
            lines.Add(sbTh.ToString());
        }

        lines.Add("end");
        lines.Add("");
        return lines;
    }

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static string F6(double v) => F(v);
    private static string F8(double v) => F(v);
}

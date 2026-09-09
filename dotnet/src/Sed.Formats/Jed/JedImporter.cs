using System.Globalization;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Formats.Jed;

public sealed record JedImportResult(JklDocument Document, string Version,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Coverage);

/// <summary>
/// Imports the native text project layout written by JED/SED. This is a
/// source-derived importer for the GPL editor fork, not an engine JKL reader.
/// Native projects do not embed their external editor template libraries.
/// </summary>
public static class JedImporter
{
    public static JedImportResult ImportFile(string path)
    {
        string text = LegacyText.ReadFile(path);
        bool mots = DetectProjectType(text) == ProjectType.MysteriesOfTheSith;
        string name = mots ? "mots.tpl" : "master.tpl";
        var templates = LoadTemplates(mots ? ProjectType.MysteriesOfTheSith : ProjectType.JediKnight,
            Path.GetDirectoryName(Path.GetFullPath(path)));
        var result = ImportText(text, templates);
        if (templates.Count == 0 && result.Document.Level.Things.Count > 0)
            result.Document.PreservationWarnings.Add($"No {name} template library was found beside the project or in the editor data folder; supply templates before exporting game entities.");
        return result;
    }

    /// <summary>Uses the same newline and section-token rules as the native parser when selecting template libraries.</summary>
    public static ProjectType DetectProjectType(string text)
    {
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var tokens = JklReader.Tokens(Clean(line));
            if (tokens.Length == 2 && tokens[0].Equals("SEC:", StringComparison.OrdinalIgnoreCase) &&
                (tokens[1].Equals("MOTS", StringComparison.OrdinalIgnoreCase) || tokens[1].Equals("ARCHLIGHTING", StringComparison.OrdinalIgnoreCase)))
                return ProjectType.MysteriesOfTheSith;
        }
        return ProjectType.JediKnight;
    }

    /// <summary>
    /// Loads the shipped game template library, then applies optional project
    /// companion definitions. Missing libraries yield an empty collection.
    /// Each call owns its templates; callers may safely add them to a new level.
    /// </summary>
    public static IReadOnlyList<Template> LoadTemplates(ProjectType game, string? companionDirectory = null)
    {
        string name = game switch
        {
            ProjectType.JediKnight => "master.tpl",
            ProjectType.MysteriesOfTheSith => "mots.tpl",
            _ => throw new NotSupportedException("Template libraries are available for Jedi Knight and Mysteries of the Sith only."),
        };
        var templates = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
        string bundled = Path.Combine(AppContext.BaseDirectory, "data", name);
        if (File.Exists(bundled)) ReadTemplates(LegacyText.ReadFile(bundled), templates);
        if (!string.IsNullOrWhiteSpace(companionDirectory))
        {
            string companion = Path.Combine(Path.GetFullPath(companionDirectory), name);
            if (File.Exists(companion)) ReadTemplates(LegacyText.ReadFile(companion), templates);
        }
        return templates.Values.ToArray();
    }

    /// <summary>Parses already captured libraries in merge order, without reopening their source files.</summary>
    public static IReadOnlyList<Template> LoadTemplateTexts(IEnumerable<string> libraries)
    {
        var templates = new Dictionary<string, Template>(StringComparer.OrdinalIgnoreCase);
        foreach (string text in libraries) ReadTemplates(text, templates);
        return templates.Values.ToArray();
    }

    public static JedImportResult ImportText(string text, IEnumerable<Template>? templates = null)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int cursor = 0;
        while (cursor < lines.Length && Clean(lines[cursor]).Length == 0) cursor++;
        if (cursor == lines.Length) throw new FormatException("Empty native editor project.");
        var signature = JklReader.Tokens(Clean(lines[cursor++]));
        if (signature.Length != 2 || !(signature[0].Equals("JED", StringComparison.OrdinalIgnoreCase) ||
            signature[0].Equals("SED", StringComparison.OrdinalIgnoreCase) || signature[0].Equals("NED", StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException("Expected a JED, SED or NED native text project header.");
        var sections = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        while (cursor < lines.Length)
        {
            string line = Clean(lines[cursor++]);
            if (line.Length == 0) continue;
            var header = JklReader.Tokens(line);
            if (line.Equals("COGGROUPS", StringComparison.OrdinalIgnoreCase)) header = ["SEC:", "COGGROUPS"];
            if (header.Length != 2 || !header[0].Equals("SEC:", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Expected native SEC: section header.");
            var body = new List<string>();
            bool ended = false;
            while (cursor < lines.Length)
            {
                line = Clean(lines[cursor++]);
                if (line.Equals("END", StringComparison.OrdinalIgnoreCase)) { ended = true; break; }
                if (line.StartsWith("SEC:", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException($"Native {header[1]} section is missing END.");
                body.Add(line);
            }
            if (!ended) throw new FormatException($"Native {header[1]} section is missing END.");
            if (!sections.TryAdd(header[1], body.ToArray())) throw new FormatException($"Duplicate native {header[1]} section.");
        }

        var level = new Level { Kind = sections.ContainsKey("MOTS") || sections.ContainsKey("ARCHLIGHTING") ? ProjectType.MysteriesOfTheSith : ProjectType.JediKnight };
        var document = new JklDocument(level);
        var warnings = document.PreservationWarnings;
        var coverage = new List<string>();
        if (templates is not null)
            foreach (var template in templates)
            {
                var copy = new Template { Name = template.Name, Parent = template.Parent, Order = template.Order };
                foreach (var value in template.Values) copy.Values.Add(value.Key, value.Value);
                copy.SourceParameters.AddRange(template.SourceParameters);
                level.Templates[copy.Name] = copy;
            }
        if (!sections.TryGetValue("HEADER", out var headerLines)) throw new FormatException("Native project has no HEADER section.");
        ReadHeader(new Rows(headerLines), level);
        if (level.Header.Version != 1) throw new NotSupportedException("Native import currently supports JK/MotS version-1 worlds only.");
        coverage.Add("HEADER");
        if (sections.TryGetValue("GEO", out var geometry)) { ReadGeometry(new Rows(geometry), level, warnings); coverage.Add("GEO"); }
        if (sections.TryGetValue("THINGS", out var things)) { ReadThings(new Rows(things), level); coverage.Add("THINGS"); }
        if (sections.TryGetValue("LIGHTS", out var lights)) { ReadLights(new Rows(lights), level, warnings); coverage.Add("LIGHTS"); }
        if (sections.TryGetValue("COGS", out var cogs)) { ReadCogs(new Rows(cogs), level); coverage.Add("COGS"); }
        if (sections.TryGetValue("LAYERS", out var layers))
        {
            var reader = new Rows(layers);
            int count = reader.Count("LAYERS");
            for (int i = 0; i < count; i++) level.Layers.Add(reader.Next());
            reader.Finish(); coverage.Add("LAYERS");
        }
        if (sections.TryGetValue("ARCHLIGHTING", out var arch))
        {
            level.ArchitecturalLighting.AddRange(ArchitecturalLightingFormat.Parse(arch));
            level.Kind = ProjectType.MysteriesOfTheSith; coverage.Add("ARCHLIGHTING");
        }
        if (sections.TryGetValue("ARCHLIGHTINGBINDINGS", out var bindings))
        {
            ReadBindings(new Rows(bindings), level, warnings);
            coverage.Add("ARCHLIGHTINGBINDINGS");
        }
        foreach (var section in sections)
        {
            if (coverage.Contains(section.Key, StringComparer.OrdinalIgnoreCase) || section.Key.Equals("MOTS", StringComparison.OrdinalIgnoreCase)) continue;
            if (section.Key.Equals("LVIS", StringComparison.OrdinalIgnoreCase))
                warnings.Add("Native layer-visibility state is editor metadata and is not represented by this importer.");
            else if (section.Key.Equals("COGGROUPS", StringComparison.OrdinalIgnoreCase))
                warnings.Add("NED COG-group editor metadata is not represented by this importer.");
            else if (section.Key.Equals("HIERARCHYDEF", StringComparison.OrdinalIgnoreCase))
            {
                var reader = new Rows(section.Value);
                if (reader.Count("NODES") != 0) throw new NotSupportedException("Native model hierarchy nodes are not supported; the project was not partially imported.");
                reader.Finish();
            }
            else throw new NotSupportedException($"Native section {section.Key} is not supported; the project was not partially imported.");
        }
        int highestLayer = level.Sectors.Select(s => s.Layer).Concat(level.Things.Select(t => t.Layer))
            .Concat(level.Lights.Select(l => l.Layer)).DefaultIfEmpty(-1).Max();
        if (highestLayer > 100_000) throw new FormatException("Native layer index exceeds the importer limit.");
        while (level.Layers.Count <= highestLayer) level.Layers.Add($"Layer{level.Layers.Count}");
        level.RenumberSectors(); level.RenumberThings(); level.RenumberLights(); level.RenumberCogs();
        // Native JED has no engine capacity field. Match the existing SED JKL writer reserve.
        level.ThingCapacity = checked(level.Things.Count + 256);
        foreach (var sector in level.Sectors) { sector.Renumber(); foreach (var surface in sector.Surfaces) surface.RecalcNormal(); }
        foreach (var surface in level.Sectors.SelectMany(s => s.Surfaces))
            if (surface.Material.Length > 0)
            {
                int index = document.Materials.FindIndex(m => m.Equals(surface.Material, StringComparison.OrdinalIgnoreCase));
                if (index < 0) { index = document.Materials.Count; document.Materials.Add(surface.Material); }
                surface.MaterialIndex = index;
            }
        if (level.Things.Count > 0 && level.Templates.Count == 0)
            warnings.Add("Native projects reference external template libraries. No templates were supplied to this import.");
        return new(document, signature[0] + " " + signature[1], warnings, coverage);
    }

    private static void ReadHeader(Rows rows, Level level)
    {
        var hdr = rows.Fields(8, "HDR");
        var h = level.Header;
        h.Version = Int(hdr[1]); h.Gravity = Single(hdr[2]); h.CeilingSky.Height = Single(hdr[3]);
        h.PerspectiveDistance = Single(hdr[4]); h.GouraudDistance = Single(hdr[5]);
        level.MasterCmp = Name(hdr[6]); level.PixelsPerUnit = Number(hdr[7]);
        if (level.MasterCmp.Length > 0) level.ColorMaps.Add(level.MasterCmp);
        var hor = rows.Fields(5, "HOR");
        h.HorizonSky.Distance = Single(hor[1]); h.HorizonSky.PixelsPerRev = Single(hor[2]);
        h.HorizonSky.Offset = new(Number(hor[3]), Number(hor[4]));
        var ceil = rows.Fields(3, "CEIL"); h.CeilingSky.Offset = new(Number(ceil[1]), Number(ceil[2]));
        var mip = rows.Fields(5, "MIP"); var lod = rows.Fields(5, "LOD");
        for (int i = 0; i < 4; i++) { h.MipmapDistances[i] = Single(mip[i + 1]); h.LodDistances[i] = Single(lod[i + 1]); }
        rows.Finish();
    }

    private static void ReadGeometry(Rows rows, Level level, List<string> warnings)
    {
        int count = rows.Count("SECS");
        bool inactiveRgb = false;
        var adjoins = new List<(Surface surface, int sector, int local)>();
        for (int i = 0; i < count; i++)
        {
            var sector = level.NewSector(); var t = rows.Fields(10);
            sector.ColorMap = Name(t[0]); sector.Sound = Name(t[1]); sector.SoundVolume = Number(t[2]);
            sector.Flags = Hex(t[3]); sector.Ambient = Grey(t[4]); sector.ExtraLight = Grey(t[5]);
            if ((sector.Flags & 0x1000) != 0)
            {
                // The native 10-field sector record stores flags but no collision
                // box. An invented bounding box is not a safe interior shortcut.
                sector.Flags &= ~0x1000L;
                warnings.Add($"Native sector {i} enables COLLIDEBOX but the native project format stores no interior collision box. The shortcut flag was cleared so the engine uses surface collision tests.");
            }
            sector.Tint = new(Single(t[6]), Single(t[7]), Single(t[8])); sector.Layer = Int(t[9]);
            if (sector.ColorMap.Length > 0 && !level.ColorMaps.Contains(sector.ColorMap, StringComparer.OrdinalIgnoreCase)) level.ColorMaps.Add(sector.ColorMap);
            int vertices = rows.Count("VXS");
            for (int v = 0; v < vertices; v++) { var xyz = rows.Fields(3); sector.AddVertex(Vector(xyz, 0)); }
            int surfaces = rows.Count("SURFS");
            for (int f = 0; f < surfaces; f++)
            {
                t = rows.Fields(13); var surface = sector.NewSurface();
                surface.Material = Name(t[0]); surface.SurfFlags = Hex(t[1]); surface.FaceFlags = Hex(t[2]);
                surface.Geo = Int(t[3]); surface.Light = Int(t[4]); surface.Tex = Int(t[5]); surface.ExtraLightIntensity = Single(t[6]);
                int corners = Int(t[7]);
                if (corners < 3 || corners > 512) throw new FormatException("Invalid native surface corner count.");
                adjoins.Add((surface, Int(t[8]), Int(t[9]))); surface.AdjoinFlags = Hex(t[10]);
                surface.UScale = Single(t[11]); surface.VScale = Single(t[12]);
                var values = JklReader.Tokens(rows.Next());
                if (values.Length != corners * 4 && values.Length != corners * 7)
                    throw new FormatException("Native surface must contain 4N vertex/UV/mono fields and optionally 3N RGB fields.");
                for (int c = 0; c < corners; c++)
                {
                    int index = Int(values[c * 4]);
                    if ((uint)index >= (uint)sector.Vertices.Count) throw new FormatException("Native surface references a missing vertex.");
                    float mono = Single(values[c * 4 + 3]);
                    var rgb = values.Length == corners * 7
                        ? new ColorF(Single(values[corners * 4 + c * 3]), Single(values[corners * 4 + c * 3 + 1]), Single(values[corners * 4 + c * 3 + 2]))
                        : new ColorF(mono, mono, mono);
                    if (level.Kind == ProjectType.JediKnight)
                    {
                        inactiveRgb |= rgb != new ColorF(mono, mono, mono);
                        rgb = new(mono, mono, mono);
                    }
                    surface.Corners.Add(new() { Vertex = sector.Vertices[index], Uv = new(Number(values[c * 4 + 1]), Number(values[c * 4 + 2])),
                        Intensity = rgb, MonoIntensity = level.Kind == ProjectType.MysteriesOfTheSith ? mono : null });
                }
            }
        }
        foreach (var (surface, sector, local) in adjoins)
        {
            if (sector == -1 && local == -1) continue;
            if ((uint)sector >= (uint)level.Sectors.Count || (uint)local >= (uint)level.Sectors[sector].Surfaces.Count)
                throw new FormatException("Native surface adjoin references a missing sector/surface.");
            surface.Adjoin = level.Sectors[sector].Surfaces[local];
        }
        rows.Finish();
        if (inactiveRgb) warnings.Add("JK uses native monochrome surface values. Inactive RGB corner values differ and remain only in the original native source; use MotS mode if those colors are intended.");
    }

    private static void ReadThings(Rows rows, Level level)
    {
        int count = rows.Count("THS");
        for (int i = 0; i < count; i++)
        {
            var t = rows.Fields(9); var thing = level.NewThing();
            thing.Template = thing.Name = t[0]; thing.Position = Vector(t, 1);
            thing.Pitch = Number(t[4]); thing.Yaw = Number(t[5]); thing.Roll = Number(t[6]); thing.Layer = Int(t[8]);
            int sector = Int(t[7]);
            if (sector != -1)
            {
                if ((uint)sector >= (uint)level.Sectors.Count) throw new FormatException("Native thing references a missing sector.");
                thing.Sector = level.Sectors[sector];
            }
            foreach (var parameter in JklReader.ParameterTokens(rows.Next(allowEmpty: true)))
            {
                int equals = parameter.IndexOf('=');
                if (equals <= 0) throw new FormatException("Invalid native thing parameter.");
                string key = parameter[..equals], value = parameter[(equals + 1)..];
                thing.Values[key] = value; thing.SourceParameters.Add(new(key, value));
            }
        }
        rows.Finish();
    }

    private static void ReadLights(Rows rows, Level level, List<string> warnings)
    {
        int count = rows.Count("LTS"); bool legacyWarning = false;
        for (int i = 0; i < count; i++)
        {
            var t = JklReader.Tokens(rows.Next());
            if (t.Length is not (10 or 12)) throw new FormatException("Native light requires a 10-field SED or 12-field legacy JED row.");
            var light = level.NewLight(); light.Position = Vector(t, 0); light.Layer = Int(t[3]);
            light.Intensity = Number(t[4]); light.Range = Number(t[5]); light.Flags = Hex(t[6]);
            int color = t.Length == 12 ? 9 : 7;
            light.Color = new(Single(t[color]), Single(t[color + 1]), Single(t[color + 2]));
            if (t.Length == 12)
            {
                _ = Number(t[7]);
                double rgbIntensity = Number(t[8]);
                if (level.Kind == ProjectType.MysteriesOfTheSith)
                {
                    legacyWarning |= rgbIntensity != light.Intensity || Number(t[7]) != light.Range;
                    light.Intensity = rgbIntensity;
                }
            }
            if (level.Kind == ProjectType.JediKnight) light.Color = ColorF.White;
        }
        if (legacyWarning) warnings.Add("Legacy MotS lights use RGB intensity and primary range. Independent white intensity and unused RGB range are not represented; imported baked surface values remain separate.");
        rows.Finish();
    }

    private static void ReadCogs(Rows rows, Level level)
    {
        int count = rows.Count("COGS");
        for (int i = 0; i < count; i++)
        {
            var tokens = JklReader.ParameterTokens(rows.Next());
            var cog = new Cog { Name = tokens[0] };
            for (int p = 1; p < tokens.Length; p++)
            {
                int colon = tokens[p].IndexOf(':'), equals = tokens[p].IndexOf('=');
                if (colon < 1 || equals <= colon + 1) throw new FormatException("Unsupported native COG symbol record; expected name:type=value.");
                cog.Values.Add(tokens[p][(equals + 1)..]);
            }
            level.Cogs.Add(cog);
        }
        rows.Finish();
    }

    private static void ReadBindings(Rows rows, Level level, List<string> warnings)
    {
        int count = rows.Count("BINDINGS"); var used = new HashSet<int>(); bool hash = false;
        for (int i = 0; i < count; i++)
        {
            var t = rows.Fields(4); int thing = Int(t[0]), index = Int(t[1]);
            if ((uint)thing >= (uint)level.Things.Count || (uint)index >= (uint)level.ArchitecturalLighting.Count || !used.Add(thing))
                throw new FormatException("Invalid or repeated native architectural-lighting binding.");
            level.Things[thing].ArchLightingIndex = index;
            level.Things[thing].ArchLightingSourceModel = Name(t[2]);
            level.Things[thing].ArchLightingSourceModelHash = Name(t[3]);
            hash |= t[3] != "-";
        }
        if (hash) warnings.Add("Native architectural-lighting SHA256 baselines are not yet verified by the .NET resource loader; only resolved model names are checked before JKL export.");
        rows.Finish();
    }

    private static void ReadTemplates(string text, Dictionary<string, Template> templates)
    {
        foreach (string line in text.Split('\n'))
        {
            var tokens = JklReader.ParameterTokens(Clean(line)); if (tokens.Length < 2) continue;
            var template = new Template { Name = tokens[0], Parent = tokens[1], Order = templates.Count };
            foreach (string parameter in tokens.Skip(2))
            {
                int equals = parameter.IndexOf('='); if (equals < 1) throw new FormatException("Invalid companion template parameter.");
                var pair = new KeyValuePair<string, string>(parameter[..equals], parameter[(equals + 1)..]);
                template.Values[pair.Key] = pair.Value; template.SourceParameters.Add(pair);
            }
            templates[template.Name] = template;
        }
    }

    private static string Clean(string line) => line.Split('#')[0].Trim();
    private static string Name(string text) => text == "-" ? string.Empty : text;
    private static int Int(string text) => JklReader.ParseInt(text);
    private static long Hex(string text) => JklReader.ParseHex(text.TrimStart('$'));
    private static double Number(string text) => JklReader.ParseFloat(text);
    private static float Single(string text)
    {
        double value = Number(text);
        return value is >= -float.MaxValue and <= float.MaxValue ? (float)value : throw new FormatException("Native number exceeds the finite float range.");
    }
    private static ColorF Grey(string text) { float value = Single(text); return new(value, value, value); }
    private static Vec3 Vector(string[] tokens, int start) => new(Number(tokens[start]), Number(tokens[start + 1]), Number(tokens[start + 2]));

    private sealed class Rows(string[] lines)
    {
        private int _position;
        public string Next(bool allowEmpty = false)
        {
            while (_position < lines.Length)
            {
                string line = lines[_position++]; if (allowEmpty || line.Length > 0) return line;
            }
            throw new FormatException("Truncated native section.");
        }
        public string[] Fields(int count, string? label = null)
        {
            var fields = JklReader.Tokens(Next());
            if (fields.Length != count || (label is not null && !fields[0].Equals(label, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"Invalid native {(label ?? "data")} row; expected {count} fields.");
            return fields;
        }
        public int Count(string label)
        {
            int count = Int(Fields(2, label)[1]);
            return count >= 0 && count <= lines.Length - _position ? count : throw new FormatException($"Invalid or truncated native {label} count.");
        }
        public void Finish()
        {
            if (lines.Skip(_position).Any(line => line.Length != 0)) throw new FormatException("Unexpected trailing native section data.");
        }
    }
}

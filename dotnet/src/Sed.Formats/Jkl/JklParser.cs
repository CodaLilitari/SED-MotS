using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Formats.Jkl;

/// <summary>
/// Parses a Sith-engine JKL level file into the editor <see cref="Level"/> model.
/// Faithful to the original SED loader (LEVEL_IO.INC): the GEORESOURCE section
/// builds global vertex/texture-vertex/surface tables which the SECTORS section
/// then references, de-duplicating vertices per sector.
///
/// Geometry, sectors, and things are parsed; materials are tracked by name.
/// COGs, lights, layers, and adjoin resolution are not required to render and
/// are skipped for now.
/// </summary>
public static class JklParser
{
    public static Level Parse(string text) => Parse(new StringReader(text));

    public static Level ParseFile(string path)
    {
        using var sr = new StreamReader(path);
        return Parse(sr);
    }

    /// <summary>Parses while recording the original text + line mappings for editable round-trip saving.</summary>
    public static JklDocument ParseDocument(string text)
    {
        var doc = new JklDocument(SplitLines(text));
        doc.Level = Parse(new StringReader(text), doc);
        JklWriter.CaptureBaseline(doc);
        return doc;
    }

    public static Level Parse(TextReader reader) => Parse(reader, null);

    private static Level Parse(TextReader reader, JklDocument? doc)
    {
        var level = new Level();
        var r = new JklReader(reader);
        var geo = new GeoData();
        var seenSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            r.SeekSection();
            if (r.Eof) break;
            var section = r.Section;
            if (!seenSections.Add(section) && section is "HEADER" or "MATERIALS" or "GEORESOURCE" or
                "SECTORS" or "TEMPLATES" or "THINGS" or "LIGHTS" or "COGS" or "LAYERS" or "ARCHLIGHTING")
                throw new FormatException($"Duplicate modeled JKL section {section}.");
            r.EnterSection();

            switch (section)
            {
                case "HEADER": LoadHeader(r, level, doc); break;
                case "MATERIALS": LoadMaterials(r, geo); break;
                case "GEORESOURCE": LoadGeometry(r, level, geo, doc); break;
                case "SECTORS": LoadSectors(r, level, geo, doc); break;
                case "TEMPLATES": LoadTemplates(r, level, doc); break;
                case "THINGS": LoadThings(r, level, doc); break;
                case "LIGHTS": LoadLights(r, level); break;
                case "COGS": LoadCogs(r, level); break;
                case "LAYERS": LoadLayers(r, level); break;
                case "ARCHLIGHTING": LoadArchitecturalLighting(r, level); break;
                default: r.SkipToNextSection(); break;
            }
        }

        ResolveAdjoins(geo);
        foreach (var surface in geo.SurfaceByGlobalIndex.Values) surface.Material = geo.GetMaterial(surface.MaterialIndex);
        if (doc is not null) doc.Materials.AddRange(geo.Materials);
        PostProcess(level);
        foreach (var thing in level.Things)
        {
            if (thing.ArchLightingIndex < -1 || thing.ArchLightingIndex >= level.ArchitecturalLighting.Count)
                throw new FormatException($"Thing {thing.Num} references missing architectural lighting object {thing.ArchLightingIndex}.");
            if (thing.ArchLightingIndex >= 0) thing.ArchLightingSourceModel = level.GetThingModel(thing);
        }
        return level;
    }

    private static ColorF Grey(double i) => new((float)i, (float)i, (float)i);

    /// <summary>Resolves surface→surface adjoin links via the parsed adjoin mirror pairs.</summary>
    private static void ResolveAdjoins(GeoData geo)
    {
        foreach (var (globalIdx, surf) in geo.SurfaceByGlobalIndex)
        {
            if ((uint)globalIdx >= (uint)geo.Surfaces.Count) continue;
            int nadj = geo.Surfaces[globalIdx].Adjoin;
            if (nadj < 0 || nadj >= geo.Adjoins.Count) continue;
            int mirror = geo.Adjoins[nadj].Mirror;
            if (mirror < 0 || mirror >= geo.Adjoins.Count) continue;
            if (geo.SurfaceByGlobalIndex.TryGetValue(geo.Adjoins[mirror].Surf, out var paired))
            {
                surf.Adjoin = paired;
                surf.AdjoinFlags = geo.Adjoins[nadj].Flags;
            }
        }
    }

    internal static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    // ---- HEADER ----

    private static void LoadHeader(JklReader r, Level level, JklDocument? doc)
    {
        while (r.Next() && !r.EndOfSection)
        {
            var t = JklReader.Tokens(r.Current);
            if (t.Length < 2) { Unmodeled(doc, "HEADER", r.Current); continue; }
            var w0 = t[0];

            if (w0 == "VERSION")
            {
                level.Header.Version = JklReader.ParseInt(t[1]);
                level.Kind = level.Header.Version is 2 or 3
                    ? ProjectType.InfernalMachine
                    : ProjectType.JediKnight;
            }
            else if (w0 == "WORLD" && t.Length >= 3 && t[1] == "GRAVITY")
                level.Header.Gravity = (float)JklReader.ParseFloat(t[2]);
            else if (w0 == "CEILING" && t.Length >= 4 && t[1] == "SKY" && t[2] == "Z")
                level.Header.CeilingSky.Height = (float)JklReader.ParseFloat(t[3]);
            else if (w0 == "CEILING" && t.Length >= 4 && t[1] == "SKY" && t[2] == "OFFSET")
            {
                level.Header.CeilingSky.Offset = new Vec2(
                    JklReader.ParseFloat(t[3]), JklReader.ParseFloat(t.ElementAtOrDefault(4) ?? "0"));
            }
            else if (w0 == "HORIZON" && t.Length >= 3 && t[1] == "DISTANCE")
                level.Header.HorizonSky.Distance = (float)JklReader.ParseFloat(t[2]);
            else if (w0 == "HORIZON" && t.Length >= 5 && t[1] == "PIXELS" && t[2] == "PER" && t[3] == "REV")
                level.Header.HorizonSky.PixelsPerRev = (float)JklReader.ParseFloat(t[4]);
            else if (w0 == "HORIZON" && t.Length >= 4 && t[1] == "SKY" && t[2] == "OFFSET")
            {
                level.Header.HorizonSky.Offset = new Vec2(
                    JklReader.ParseFloat(t[3]), JklReader.ParseFloat(t.ElementAtOrDefault(4) ?? "0"));
            }
            else if (w0 == "MIPMAP" && t.Length >= 6 && t[1] == "DISTANCES")
            {
                for (int i = 0; i < 4; i++)
                    level.Header.MipmapDistances[i] = (float)JklReader.ParseFloat(t.ElementAtOrDefault(2 + i) ?? "0");
            }
            else if (w0 == "LOD" && t.Length >= 6 && t[1] == "DISTANCES")
            {
                for (int i = 0; i < 4; i++)
                    level.Header.LodDistances[i] = (float)JklReader.ParseFloat(t.ElementAtOrDefault(2 + i) ?? "0");
            }
            else if (w0 == "PERSPECTIVE" && t.Length >= 3 && t[1] == "DISTANCE")
                level.Header.PerspectiveDistance = (float)JklReader.ParseFloat(t[2]);
            else if (w0 == "GOURAUD" && t.Length >= 3 && t[1] == "DISTANCE")
                level.Header.GouraudDistance = (float)JklReader.ParseFloat(t[2]);
            else if (w0 == "FOG" && t.Length >= 8)
            {
                level.Header.Fog.Enabled = JklReader.ParseInt(t[1]) != 0;
                level.Header.Fog.Color = new ColorF(
                    (float)JklReader.ParseFloat(t[2]), (float)JklReader.ParseFloat(t[3]),
                    (float)JklReader.ParseFloat(t[4]));
                level.Header.Fog.Start = JklReader.ParseFloat(t.ElementAtOrDefault(6) ?? "0");
                level.Header.Fog.End = JklReader.ParseFloat(t.ElementAtOrDefault(7) ?? "0");
            }
            else Unmodeled(doc, "HEADER", r.Current);
        }
    }

    // ---- MATERIALS ----

    private static void LoadMaterials(JklReader r, GeoData geo)
    {
        if (!r.Next()) return;
        int n = LastInt(r.Current);
        for (int i = 0; i < n; i++)
        {
            if (!r.Next(upper: false) || r.EndOfSection) break;
            var body = JklReader.StripIndex(r.Current).Trim();
            int dot = body.IndexOf(".mat", StringComparison.OrdinalIgnoreCase);
            geo.Materials.Add(dot >= 0 ? body[..(dot + 4)].Trim() : JklReader.FirstWord(body));
        }
    }

    // ---- GEORESOURCE ----

    private static void LoadGeometry(JklReader r, Level level, GeoData geo, JklDocument? doc)
    {
        bool ijim = level.Kind == ProjectType.InfernalMachine;

        // Colormaps (absent for IJIM): "i: name.cmp"
        if (!ijim)
        {
            RequireData(r);
            int nc = LastInt(r.Current);
            for (int i = 0; i < nc; i++)
            {
                RequireData(r);
                var name = JklReader.WordAt(JklReader.StripIndex(r.Current), 0);
                if (name.Length > 0) level.ColorMaps.Add(name);
            }
        }

        // World vertices: "i: x y z"
        RequireData(r);
        int nv = LastInt(r.Current);
        for (int i = 0; i < nv; i++)
        {
            RequireData(r);
            var t = JklReader.Tokens(JklReader.StripIndex(r.Current));
            var pos = new Vec3(
                JklReader.ParseFloat(t.ElementAtOrDefault(0) ?? "0"),
                JklReader.ParseFloat(t.ElementAtOrDefault(1) ?? "0"),
                JklReader.ParseFloat(t.ElementAtOrDefault(2) ?? "0"));
            geo.Vertices.Add(pos);
            if (doc is not null) { doc.VertexLine[i] = r.LineNumber - 1; doc.VertexOriginal[i] = pos; }
        }

        // Texture vertices: "i: u v"
        RequireData(r);
        int nt = LastInt(r.Current);
        for (int i = 0; i < nt; i++)
        {
            RequireData(r);
            var t = JklReader.Tokens(JklReader.StripIndex(r.Current));
            geo.TexVertices.Add(new Vec2(
                JklReader.ParseFloat(t.ElementAtOrDefault(0) ?? "0"),
                JklReader.ParseFloat(t.ElementAtOrDefault(1) ?? "0")));
        }

        // Adjoins: "i: flags mirror"
        RequireData(r);
        int na = LastInt(r.Current);
        for (int i = 0; i < na; i++)
        {
            RequireData(r);
            var t = JklReader.Tokens(JklReader.StripIndex(r.Current));
            geo.Adjoins.Add(new TempAdjoin
            {
                Flags = JklReader.ParseHex(t.ElementAtOrDefault(0) ?? "0"),
                Mirror = JklReader.ParseInt(t.ElementAtOrDefault(1) ?? "-1"),
            });
        }

        // Surfaces.
        RequireData(r);
        int ns = LastInt(r.Current);
        for (int i = 0; i < ns; i++)
        {
            RequireData(r);
            var ts = ParseSurface(JklReader.StripIndex(r.Current), level.Header.Version, ijim, level);
            geo.Surfaces.Add(ts);
            if (ts.Adjoin >= 0 && ts.Adjoin < geo.Adjoins.Count) geo.Adjoins[ts.Adjoin].Surf = i;
        }

        // Normals: one line per surface — skip.
        for (int i = 0; i < ns; i++) RequireData(r);
    }

    private static TempSurface ParseSurface(string line, int version, bool ijim, Level level)
    {
        // Vertex refs use "vx,tvx"; turning commas into spaces lets us read pairs.
        var t = JklReader.Tokens(line.Replace(',', ' '));
        var s = new TempSurface();
        int k = 0;
        string Token() => k < t.Length ? t[k++] : throw new FormatException("Truncated JKL surface.");
        int Int() => JklReader.ParseInt(Token());
        long Hex() => JklReader.ParseHex(Token());
        double Flt() => JklReader.ParseFloat(Token());

        s.Material = Int();
        s.SurfFlags = Hex();
        s.FaceFlags = Hex();
        s.Geo = Int();
        s.Light = Int();
        s.Tex = Int();
        s.Adjoin = Int();

        float extraA;
        if (ijim)
        {
            float er = (float)Flt(), eg = (float)Flt(), eb = (float)Flt();
            extraA = (float)Flt();
            s.ExtraLight = new ColorF(er, eg, eb);
        }
        else
        {
            extraA = (float)Flt(); // single intensity
            s.ExtraLight = new ColorF(extraA, extraA, extraA);
        }

        int nvx = Int();
        if (nvx < 3 || nvx > 512 || nvx > (t.Length - k) / 2)
            throw new FormatException($"Invalid JKL surface vertex count {nvx}.");
        for (int j = 0; j < nvx; j++)
        {
            s.Vxs.Add(Int());
            s.Tvxs.Add(Int());
        }

        // Remaining tokens are per-vertex light intensities.
        var il = new List<double>();
        while (k < t.Length) il.Add(Flt());
        if (il.Any(value => value < -float.MaxValue || value > float.MaxValue))
            throw new FormatException("Surface light component is outside the finite engine float range.");

        int components = il.Count == nvx && !ijim ? 1 :
            il.Count == nvx * (ijim ? 3 : 4) ? (ijim ? 3 : 4) : 0;
        if (components == 0)
            throw new FormatException($"Surface has {il.Count} light components for {nvx} vertices; expected {(ijim ? "3N" : "N or 4N")}.");
        if (components > 1)
        {
            // MotS (version 1 with extra data) or IJIM: RGB(A) per vertex.
            if (version == 1) level.Kind = ProjectType.MysteriesOfTheSith;
            int off = 0;
            for (int j = 0; j < nvx; j++)
            {
                s.MonoIntensities.Add(version == 1 ? (float)il[off++] : null);
                float cr = (float)il[off++];
                float cg = (float)il[off++];
                float cb = (float)il[off++];
                s.Intensities.Add(new ColorF(cr, cg, cb));
            }
        }
        else
        {
            for (int j = 0; j < nvx; j++)
            {
                float a = (float)il.ElementAtOrDefault(j);
                s.Intensities.Add(new ColorF(a, a, a));
                s.MonoIntensities.Add(null);
            }
        }

        return s;
    }

    // ---- SECTORS ----

    private static void LoadSectors(JklReader r, Level level, GeoData geo, JklDocument? doc)
    {
        if (!r.Next()) return;
        int numSecs = LastInt(r.Current);

        for (int i = 0; i < numSecs; i++)
        {
            RequireData(r); // "SECTOR i"
            var sector = level.NewSector();
            var globalToLocal = new Dictionary<int, Vertex>();

            while (true)
            {
                RequireData(r);
                var t = JklReader.Tokens(r.Current);
                var w = t[0];

                if (w == "SURFACES")
                {
                    int bsurf = JklReader.ParseInt(t.ElementAtOrDefault(1) ?? "0");
                    int nsurfs = JklReader.ParseInt(t.ElementAtOrDefault(2) ?? "0");
                    BuildSectorSurfaces(sector, geo, bsurf, nsurfs, globalToLocal);
                    break;
                }
                if (w == "VERTICES")
                {
                    int nvx = JklReader.ParseInt(t.ElementAtOrDefault(1) ?? "0");
                    if (nvx < 0 || nvx > geo.Vertices.Count) throw new FormatException("Invalid sector vertex count.");
                    for (int j = 0; j < nvx; j++)
                    {
                        RequireData(r);
                        int index = JklReader.ParseInt(JklReader.StripIndex(r.Current));
                        if ((uint)index >= (uint)geo.Vertices.Count) throw new FormatException("Sector references missing world vertex.");
                        if (!globalToLocal.ContainsKey(index))
                        {
                            var vertex = sector.AddVertex(geo.Vertices[index]);
                            vertex.SourceIndex = index;
                            globalToLocal.Add(index, vertex);
                        }
                    }
                    continue;
                }
                if (w == "FLAGS") sector.Flags = JklReader.ParseHex(t.ElementAtOrDefault(1) ?? "0");
                else if (w == "TINT" && t.Length >= 4)
                    sector.Tint = new ColorF((float)JklReader.ParseFloat(t[1]),
                        (float)JklReader.ParseFloat(t[2]), (float)JklReader.ParseFloat(t[3]));
                else if (w == "AMBIENT" && t.Length >= 3) // "AMBIENT LIGHT i" (JK: single intensity)
                    sector.Ambient = Grey(JklReader.ParseFloat(t[2]));
                else if (w == "EXTRA" && t.Length >= 3)  // "EXTRA LIGHT i"
                    sector.ExtraLight = Grey(JklReader.ParseFloat(t[2]));
                else if (w == "COLORMAP" && t.Length >= 2)
                {
                    int ci = JklReader.ParseInt(t[1]);
                    sector.ColorMap = (uint)ci < (uint)level.ColorMaps.Count ? level.ColorMaps[ci] : string.Empty;
                }
                else if (w == "SOUND" && t.Length >= 3)
                {
                    sector.Sound = t[1];
                    sector.SoundVolume = JklReader.ParseFloat(t[2]);
                }
                else if (w == "THRUST") sector.Thrust = ReadVector(t, 1);
                else if (w is "COLLIDEBOX" or "BOUNDBOX")
                {
                    var box = new Box(ReadVector(t, 1), ReadVector(t, 4));
                    if (w == "COLLIDEBOX") { sector.CollideBox = box; sector.HasCollideBox = true; }
                    else sector.BoundBox = box;
                }
                else if (w == "CENTER") sector.Center = ReadVector(t, 1);
                else if (w == "RADIUS" && t.Length == 2) sector.Radius = JklReader.ParseFloat(t[1]);
                else Unmodeled(doc, "SECTORS", r.Current);
            }
            sector.BoundsSourceVertices = sector.Vertices.Select(v => v.Position).ToArray();
        }
    }

    private static void BuildSectorSurfaces(Sector sector, GeoData geo, int bsurf, int nsurfs,
        Dictionary<int, Vertex> globalToLocal)
    {
        if (bsurf < 0 || nsurfs < 0 || bsurf > geo.Surfaces.Count - nsurfs)
            throw new FormatException("Sector references an invalid surface range.");
        for (int isurf = bsurf; isurf < bsurf + nsurfs; isurf++)
        {
            if ((uint)isurf >= (uint)geo.Surfaces.Count) continue;
            var ts = geo.Surfaces[isurf];
            var surf = sector.NewSurface();
            surf.Material = geo.GetMaterial(ts.Material);
            surf.MaterialIndex = ts.Material;
            surf.SurfFlags = ts.SurfFlags;
            surf.FaceFlags = ts.FaceFlags;
            surf.Geo = ts.Geo;
            surf.Light = ts.Light;
            surf.Tex = ts.Tex;
            surf.ExtraLightIntensity = ts.ExtraLight.R;
            geo.SurfaceByGlobalIndex[isurf] = surf;

            for (int j = 0; j < ts.Vxs.Count; j++)
            {
                int gidx = ts.Vxs[j];
                if (!globalToLocal.TryGetValue(gidx, out var vertex))
                {
                    if ((uint)gidx >= (uint)geo.Vertices.Count) throw new FormatException("Surface references a missing world vertex.");
                    var pos = geo.Vertices[gidx];
                    vertex = sector.AddVertex(pos);
                    vertex.SourceIndex = gidx;
                    globalToLocal[gidx] = vertex;
                }

                int tvx = ts.Tvxs[j];
                var uv = (uint)tvx < (uint)geo.TexVertices.Count ? geo.TexVertices[tvx] : Vec2.Zero;
                surf.Corners.Add(new Surface.Corner
                {
                    Vertex = vertex,
                    Uv = new TexVertex(uv.X, uv.Y),
                    Intensity = j < ts.Intensities.Count ? ts.Intensities[j] : ColorF.White,
                    MonoIntensity = ts.MonoIntensities[j],
                });
            }
        }
    }

    // ---- TEMPLATES ----

    private static void LoadTemplates(JklReader r, Level level, JklDocument? doc)
    {
        if (!r.Next()) return; // "World templates n"
        int n = LastInt(r.Current);
        for (int i = 0; i < n; i++)
        {
            if (!r.Next(upper: false) || r.EndOfSection) break;
            var t = JklReader.ParameterTokens(r.Current);
            ReportLegacyParameters(t, r.LineNumber, doc);
            if (t.Length < 2 || t[0] is "#" or "//") continue;

            var tpl = new Template { Name = t[0], Parent = t[1], Order = level.Templates.Count };
            for (int p = 2; p < t.Length; p++)
            {
                int eq = t[p].IndexOf('=');
                if (eq > 0)
                {
                    tpl.Values[t[p][..eq]] = t[p][(eq + 1)..];
                    tpl.SourceParameters.Add(new(t[p][..eq], t[p][(eq + 1)..]));
                }
            }
            level.Templates[tpl.Name] = tpl;
        }
    }

    // ---- THINGS ----

    private static void LoadThings(JklReader r, Level level, JklDocument? doc)
    {
        if (!r.Next()) return;
        if (doc is not null) doc.ThingsCountLine = r.LineNumber - 1;
        int n = LastInt(r.Current);
        level.ThingCapacity = n;
        for (int i = 0; i < n; i++)
        {
            if (!r.Next(upper: false) || r.EndOfSection) break;
            var t = JklReader.ParameterTokens(r.Current);
            ReportLegacyParameters(t, r.LineNumber, doc);
            if (t.Length < 10) continue;

            var thing = level.NewThing();
            if (doc is not null) doc.ThingLine[thing.Num] = r.LineNumber - 1;
            // t[0]=index, t[1]=template, t[2]=name, t[3..5]=xyz, t[6..8]=pyr, t[9]=sector
            thing.Template = t[1];
            thing.Name = t[2];
            thing.Position = new Vec3(
                JklReader.ParseFloat(t[3]), JklReader.ParseFloat(t[4]), JklReader.ParseFloat(t[5]));
            thing.Pitch = JklReader.ParseFloat(t[6]);
            thing.Yaw = JklReader.ParseFloat(t[7]);
            thing.Roll = JklReader.ParseFloat(t[8]);
            int sec = JklReader.ParseInt(t[9]);
            if ((uint)sec < (uint)level.Sectors.Count) thing.Sector = level.Sectors[sec];
            else if (sec != -1) throw new FormatException($"Thing {i} references missing sector {sec}.");

            int parameterStart = 10;
            if (level.Header.Version == 1 && t.Length > 10 && !t[10].Contains('='))
            {
                thing.ArchLightingIndex = JklReader.ParseInt(t[10]);
                level.Kind = ProjectType.MysteriesOfTheSith;
                parameterStart++;
            }
            for (int p = parameterStart; p < t.Length; p++)
            {
                int eq = t[p].IndexOf('=');
                if (eq <= 0) continue;
                thing.Values[t[p][..eq]] = t[p][(eq + 1)..];
                thing.SourceParameters.Add(new(t[p][..eq], t[p][(eq + 1)..]));
            }
        }
    }

    private static void ReportLegacyParameters(string[] tokens, int lineNumber, JklDocument? document)
    {
        if (tokens.Length > 0 && JklReader.IsLegacyUnterminatedFrameToken(tokens[^1]))
            document?.PreservationWarnings.Add($"Line {lineNumber}: a legacy final frame parameter has six finite coordinates but no closing parenthesis. Its original value is retained, matching the engine's six-conversion parser.");
        foreach (string token in tokens)
            if (JklReader.IsLegacyUnterminatedEyeOffsetToken(token))
                document?.PreservationWarnings.Add($"Line {lineNumber}: a legacy eyeoffset parameter has three finite coordinates but no closing parenthesis. Its original value is retained, matching the engine's three-conversion parser; following parameters remain separate.");
    }

    // ---- LIGHTS ----

    private static void LoadLights(JklReader r, Level level)
    {
        if (!r.Next()) return;
        int n = LastInt(r.Current);
        for (int i = 0; i < n; i++)
        {
            if (!r.Next(upper: false) || r.EndOfSection) break;
            var t = JklReader.Tokens(JklReader.StripIndex(r.Current));
            if (t.Length < 7) continue;

            var light = level.NewLight();
            // JK:    flags(hex) layer x y z range intensity
            // MotS:  flags(hex) layer x y z range intensity r g b
            light.Flags = JklReader.ParseHex(t[0]);
            light.Layer = JklReader.ParseInt(t.ElementAtOrDefault(1) ?? "0");
            light.Position = new Vec3(
                JklReader.ParseFloat(t.ElementAtOrDefault(2) ?? "0"),
                JklReader.ParseFloat(t.ElementAtOrDefault(3) ?? "0"),
                JklReader.ParseFloat(t.ElementAtOrDefault(4) ?? "0"));
            light.Range = JklReader.ParseFloat(t.ElementAtOrDefault(5) ?? "0");
            light.Intensity = JklReader.ParseFloat(t.ElementAtOrDefault(6) ?? "1");
            if (t.Length >= 10)
            {
                light.Color = new ColorF(
                    (float)JklReader.ParseFloat(t[7]),
                    (float)JklReader.ParseFloat(t[8]),
                    (float)JklReader.ParseFloat(t[9]));
            }
        }
    }

    // ---- COGS ----

    private static void LoadCogs(JklReader r, Level level)
    {
        if (!r.Next()) return;
        int n = LastInt(r.Current);
        for (int i = 0; i < n; i++)
        {
            if (!r.Next(upper: false) || r.EndOfSection) break;
            var body = JklReader.StripIndex(r.Current).Trim();
            var t = JklReader.Tokens(body);
            if (t.Length == 0) continue;

            var cog = new Cog { Name = t[0] };
            for (int p = 1; p < t.Length; p++)
                cog.Values.Add(t[p]);
            level.Cogs.Add(cog);
        }
    }

    // ---- LAYERS ----

    private static void LoadLayers(JklReader r, Level level)
    {
        if (!r.Next()) return;
        int n = LastInt(r.Current);
        for (int i = 0; i < n; i++)
        {
            if (!r.Next(upper: false, preserveComment: true) || r.EndOfSection)
                throw new FormatException("Truncated editor layers.");
            level.Layers.Add(r.Current.TrimStart('#').Trim());

            // Sector indices line: "count:\tidx\tidx..."
            if (!r.Next() || r.EndOfSection) break;
            foreach (var idx in ParseIndexLine(r.Current))
                if ((uint)idx < (uint)level.Sectors.Count) level.Sectors[idx].Layer = i;
                else throw new FormatException("Layer references a missing sector.");

            // Thing indices line
            if (!r.Next() || r.EndOfSection) break;
            foreach (var idx in ParseIndexLine(r.Current))
                if ((uint)idx < (uint)level.Things.Count) level.Things[idx].Layer = i;
                else throw new FormatException("Layer references a missing thing.");
        }

        EnsureLayersExist(level);
    }

    private static IEnumerable<int> ParseIndexLine(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) throw new FormatException("Layer membership is missing its count.");
        var body = colon >= 0 ? line[(colon + 1)..] : line;
        var tokens = JklReader.Tokens(body);
        if (JklReader.ParseInt(line[..colon]) != tokens.Length) throw new FormatException("Layer membership count mismatch.");
        foreach (var t in tokens)
            yield return JklReader.ParseInt(t);
    }

    private static void EnsureLayersExist(Level level)
    {
        int maxLayer = -1;
        foreach (var s in level.Sectors) maxLayer = System.Math.Max(maxLayer, s.Layer);
        foreach (var t in level.Things) maxLayer = System.Math.Max(maxLayer, t.Layer);
        while (level.Layers.Count <= maxLayer)
            level.Layers.Add($"Layer{level.Layers.Count}");
    }

    // ---- post ----

    private static void PostProcess(Level level)
    {
        level.RenumberSectors();
        level.RenumberThings();
        level.RenumberLights();
        level.RenumberCogs();
        foreach (var sector in level.Sectors)
        {
            sector.Renumber();
            foreach (var surf in sector.Surfaces)
                surf.RecalcNormal();
        }
    }

    private static int LastInt(string line)
    {
        var t = JklReader.Tokens(line);
        int count = t.Length == 0 ? 0 : JklReader.ParseInt(t[^1]);
        return count is >= 0 and <= 1_000_000 ? count : throw new FormatException("JKL count is negative or exceeds the parser limit.");
    }

    private static void RequireData(JklReader r)
    {
        if (!r.Next() || r.EndOfSection) throw new FormatException($"Unexpected section end at line {r.LineNumber}.");
    }

    private static void Unmodeled(JklDocument? doc, string section, string line)
    {
        if (doc is null) return;
        doc.SectionsWithUnmodeledFields.Add(section);
        doc.PreservationWarnings.Add($"{section} contains an unmodeled field: {line}. Editing this section is refused to preserve it.");
    }

    private static Vec3 ReadVector(string[] tokens, int start)
    {
        if (tokens.Length < start + 3) throw new FormatException("Truncated sector vector or bounds.");
        return new(JklReader.ParseFloat(tokens[start]), JklReader.ParseFloat(tokens[start + 1]), JklReader.ParseFloat(tokens[start + 2]));
    }

    private static void LoadArchitecturalLighting(JklReader r, Level level)
    {
        if (level.ArchitecturalLighting.Count != 0) throw new FormatException("Duplicate architectural lighting section.");
        var lines = new List<string>();
        while (r.Next(upper: false) && !r.EndOfSection) lines.Add(r.Current);
        if (!r.Current.Equals("END", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Architectural lighting section is missing END.");
        level.ArchitecturalLighting.AddRange(ArchitecturalLightingFormat.Parse(lines));
        level.Kind = ProjectType.MysteriesOfTheSith;
    }
}

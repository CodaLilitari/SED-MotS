using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Validation;
using Sed.Formats.Jkl;
using Sed.Formats.Jed;
using Sed.Formats.Gob;

namespace Sed.Formats.Automation;

public sealed class AgentRequestException(string message) : Exception(message);
public sealed class AgentConflictException(string message) : Exception(message);
public sealed class AgentValidationException(string message, object issues) : Exception(message)
{
    public object Issues { get; } = issues;
}

public sealed record PreparedAgentEdit(string InputPath, string InputSha256, string OutputText,
    object Report);

/// <summary>
/// Headless document operations. Edits use the GUI's Core commands on an isolated
/// parsed document. Planning never writes; committing creates a new file atomically.
/// Numeric selectors are valid only for the exact input hash in the request.
/// </summary>
public static class AgentWorkspace
{
    public const int SchemaVersion = 1;
    public const int MaxOperations = 1000;
    public const int MaxRequestBytes = 1024 * 1024;
    public const long MaxLevelBytes = 128L * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static (JklDocument Document, string Hash) Load(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".jkl" or ".jed" or ".sed" or ".ned"))
            throw new AgentRequestException("Read a JKL or supported text JED/SED/NED project. Use archive/extract for GOB/GOO contents.");
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("The level file was not found.", path);
        if (info.Length > MaxLevelBytes) throw new AgentRequestException("The level exceeds the 128 MiB input limit.");
        var bytes = File.ReadAllBytes(path);
        if (extension != ".jkl")
        {
            var import = JedImporter.ImportFile(path);
            if (Hash(File.ReadAllBytes(path)) != Hash(bytes))
                throw new AgentConflictException("The legacy project changed during import.");
            if (!ReferenceEquals(import.Document.PreservationWarnings, import.Warnings))
                import.Document.PreservationWarnings.AddRange(import.Warnings);
            return (import.Document, Hash(bytes));
        }
        return (JklParser.ParseDocument(LegacyText.Decode(bytes)), Hash(bytes));
    }

    public static object Inspect(string path, int? sectorIndex = null)
    {
        var (doc, hash) = Load(path);
        var level = doc.Level;
        object? detail = null;
        if (sectorIndex is int index)
        {
            var sector = At(level.Sectors, index, "sector");
            detail = new
            {
                index, sector.Num, sector.Flags,
                vertices = sector.Vertices.Select((v, i) => new { index = i, position = Position(v.Position) }),
                surfaces = sector.Surfaces.Select((s, i) => new
                {
                    index = i, s.Material, s.MaterialIndex, s.SurfFlags, s.FaceFlags,
                    adjoinSector = s.Adjoin is null ? -1 : level.Sectors.IndexOf(s.Adjoin.Sector),
                    corners = s.Corners.Select(c => new
                    {
                        vertex = sector.Vertices.IndexOf(c.Vertex), uv = new[] { c.Uv.U, c.Uv.V },
                        mono = c.MonoIntensity, rgb = new[] { c.Intensity.R, c.Intensity.G, c.Intensity.B },
                    }),
                }),
            };
        }
        return new
        {
            schemaVersion = SchemaVersion, inputSha256 = hash, game = level.Kind.ToString(),
            counts = Counts(level), materials = doc.Materials,
            preservationWarnings = doc.PreservationWarnings,
            sectors = level.Sectors.Select((s, i) => new { index = i, s.Num, vertices = s.Vertices.Count, surfaces = s.Surfaces.Count }),
            things = level.Things.Select((t, i) => new
            {
                index = i, t.Name, t.Template, position = Position(t.Position),
                sector = t.Sector is null ? -1 : level.Sectors.IndexOf(t.Sector), t.ArchLightingIndex,
            }),
            detail,
        };
    }

    public static object Validate(string path)
    {
        var (doc, hash) = Load(path);
        var issues = ConsistencyChecker.Check(doc.Level);
        return new { schemaVersion = SchemaVersion, inputSha256 = hash,
            valid = issues.All(i => i.Severity != IssueSeverity.Error), issues = DescribeIssues(issues),
            preservationWarnings = doc.PreservationWarnings };
    }

    public static PreparedAgentEdit ConvertDocument(string path)
    {
        var (doc, hash) = Load(path);
        string output = JklWriter.Build(doc);
        var reopened = JklParser.ParseDocument(output);
        var issues = ConsistencyChecker.Check(reopened.Level);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            throw new AgentValidationException("Converted output has validation errors. Inspect the project before exporting.", DescribeIssues(issues));
        return new PreparedAgentEdit(Path.GetFullPath(path), hash, output, new
        {
            schemaVersion = 1, inputSha256 = hash, counts = Counts(reopened.Level),
            preservationWarnings = doc.PreservationWarnings, issues = DescribeIssues(issues),
            note = "Legacy conversion uses the recorded importer coverage. Editor-only and unsupported records can produce warnings; this is not a gameplay test.",
        });
    }

    public static object InspectArchive(string path)
    {
        using var stream = File.OpenRead(path);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        using var archive = new GobArchive(stream);
        return new { schemaVersion = 1, inputSha256 = hash,
            levels = archive.FindByExtension(".jkl").Select(e => new { name = e.Name, bytes = e.Length }) };
    }

    public static object ExtractLevel(string archivePath, string entryName, string outputPath)
    {
        using var archive = GobArchive.Open(archivePath);
        var entries = archive.Entries.Where(e => e.NormalizedName == entryName.Replace('\\', '/').ToLowerInvariant()).ToArray();
        if (entries.Length != 1 || !entries[0].NormalizedName.EndsWith(".jkl", StringComparison.Ordinal))
            throw new AgentRequestException("Specify one exact JKL entry name from archive; ambiguous or non-JKL entries are refused.");
        if (entries[0].Length > MaxLevelBytes) throw new AgentRequestException("The archived level exceeds the 128 MiB limit.");
        byte[] bytes = archive.ReadBytes(entries[0]);
        // Extraction is byte-for-byte recovery, without format rewriting.
        WriteNewBytes(outputPath, bytes);
        return new { schemaVersion = 1, written = Path.GetFullPath(outputPath), sourceEntry = entries[0].Name,
            bytes = bytes.Length, inputSha256 = Hash(bytes) };
    }

    public static PreparedAgentEdit Plan(string path, string requestText)
    {
        if (Encoding.UTF8.GetByteCount(requestText) > MaxRequestBytes)
            throw new AgentRequestException("Patch exceeds the 1 MiB request limit.");
        using var request = JsonDocument.Parse(requestText, new JsonDocumentOptions { MaxDepth = 16 });
        var root = request.RootElement;
        Fields(root, "schemaVersion", "inputSha256", "operations");
        if (Integer(root, "schemaVersion") != SchemaVersion) throw new AgentRequestException("Unsupported patch schemaVersion.");
        var expected = String(root, "inputSha256");
        if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
            throw new AgentRequestException("inputSha256 must be a 64-digit SHA256 from inspect.");
        var (doc, hash) = Load(path);
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException("The input changed since inspection. Inspect the current file and regenerate the patch.");
        var operations = root.GetProperty("operations");
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > MaxOperations)
            throw new AgentRequestException("operations must contain between 1 and 1000 operations.");

        var before = Counts(doc.Level);
        var history = new EditHistory();
        var changes = new List<object>();
        int operationIndex = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            // Build each command against the current isolated model so later
            // operations can address objects appended by earlier operations.
            var command = PrepareOperation(doc, operation);
            history.Do(command);
            changes.Add(new { index = operationIndex++, operation = String(operation, "op"), command = command.Name });
        }
        var issues = ConsistencyChecker.Check(doc.Level);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            throw new AgentValidationException("The proposed edits do not pass level validation. No file was written.", DescribeIssues(issues));
        var text = JklWriter.Build(doc);
        var reopened = JklParser.ParseDocument(text);
        var roundTripIssues = ConsistencyChecker.Check(reopened.Level);
        if (roundTripIssues.Any(i => i.Severity == IssueSeverity.Error))
            throw new AgentValidationException("The serialized level does not pass validation. No file was written.", DescribeIssues(roundTripIssues));
        if (reopened.Level.Sectors.Count != doc.Level.Sectors.Count || reopened.Level.Things.Count != doc.Level.Things.Count)
            throw new AgentValidationException("The serialized level changed its sector or thing count.", Array.Empty<object>());
        var outputHash = Hash(new UTF8Encoding(false).GetBytes(text));
        return new PreparedAgentEdit(Path.GetFullPath(path), hash, text, new
        {
            schemaVersion = SchemaVersion, inputSha256 = hash, outputSha256 = outputHash,
            before, after = Counts(reopened.Level), changes, issues = DescribeIssues(roundTripIssues),
            preservationWarnings = doc.PreservationWarnings,
            note = "Planning uses the same Core editing commands as the GUI. It validates structure, not gameplay or resource availability. Moved models may require relighting.",
        });
    }

    public static object Commit(PreparedAgentEdit edit, string outputPath)
    {
        // Output creation is deliberately no-clobber. An agent can produce a
        // reviewable result without overwriting the user's original project.
        if (Hash(File.ReadAllBytes(edit.InputPath)) != edit.InputSha256)
            throw new AgentConflictException("The input changed after planning. No output was written.");
        WriteNew(outputPath, edit.OutputText);
        return new { schemaVersion = SchemaVersion, written = Path.GetFullPath(outputPath), plan = edit.Report };
    }

    public static object Create(string outputPath, string game)
    {
        var level = LevelFactory.CreateUntexturedRoom(game switch
        {
            "mots" => ProjectType.MysteriesOfTheSith,
            "jk" => ProjectType.JediKnight,
            _ => throw new AgentRequestException("The new-level game must be mots or jk."),
        });
        foreach (var template in JedImporter.LoadTemplates(level.Kind))
            level.Templates[template.Name] = template;
        var text = JklWriter.Build(new JklDocument(level));
        WriteNew(outputPath, text);
        return new { schemaVersion = SchemaVersion, written = Path.GetFullPath(outputPath),
            inputSha256 = Hash(new UTF8Encoding(false).GetBytes(text)), counts = Counts(level),
            note = "Untextured room scaffold. Add resources, templates, a player start, and an episode before gameplay testing." };
    }

    private static void WriteNew(string path, string text)
        => WriteNewBytes(path, new UTF8Encoding(false).GetBytes(text));

    private static void WriteNewBytes(string path, byte[] bytes)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".jkl", StringComparison.OrdinalIgnoreCase))
            throw new AgentRequestException("Output must have the .jkl extension.");
        if (File.Exists(path) || Directory.Exists(path)) throw new AgentConflictException("Output already exists; choose a new output file.");
        var parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent)) throw new AgentRequestException("Output directory does not exist.");
        var temporary = Path.Combine(parent, ".sed-agent-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEditCommand PrepareOperation(JklDocument doc, JsonElement op)
    {
        var level = doc.Level;
        var name = String(op, "op");
        Thing Thing() => At(level.Things, Integer(op, "thing"), "thing");
        Sector Sector() => At(level.Sectors, Integer(op, "sector"), "sector");
        Surface Surface() => At(Sector().Surfaces, Integer(op, "surface"), "surface");
        switch (name)
        {
            case "setThingPosition":
                Fields(op, "op", "thing", "position", "sector");
                return new CompositeCommand("Place thing", new IEditCommand[] {
                    new SetThingPositionCommand(Thing(), Vector(op, "position")), new SetThingSectorCommand(Thing(), Sector()) });
            case "setThingOrientation":
                Fields(op, "op", "thing", "orientation");
                var orientation = Vector(op, "orientation");
                return new SetThingOrientationCommand(Thing(), orientation.X, orientation.Y, orientation.Z);
            case "setThingName":
                Fields(op, "op", "thing", "name");
                return new SetThingNameCommand(Thing(), Token(op, "name"));
            case "setThingTemplate":
                Fields(op, "op", "thing", "template");
                var template = Token(op, "template");
                if (!level.Templates.ContainsKey(template)) throw new AgentRequestException("Template is not defined in this level.");
                return new SetThingTemplateCommand(Thing(), template);
            case "setThingValue":
                Fields(op, "op", "thing", "key", "value");
                var key = Token(op, "key");
                if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw new AgentRequestException("Invalid template parameter key.");
                return new SetThingValueCommand(Thing(), key, Token(op, "value"));
            case "clearThingArchitecture":
                Fields(op, "op", "thing");
                return new ClearThingArchitectureCommand(Thing());
            case "setVertexPosition":
                Fields(op, "op", "sector", "vertex", "position");
                return new SetVertexPositionCommand(At(Sector().Vertices, Integer(op, "vertex"), "vertex"), Vector(op, "position"));
            case "setVertexLight":
                Fields(op, "op", "sector", "vertex", "channel", "value");
                var channel = String(op, "channel") switch { "mono" => VertexLightComponent.Mono,
                    "red" => VertexLightComponent.Red, "green" => VertexLightComponent.Green, "blue" => VertexLightComponent.Blue,
                    _ => throw new AgentRequestException("channel must be mono, red, green or blue.") };
                return new SetVertexLightComponentCommand(Sector(), At(Sector().Vertices, Integer(op, "vertex"), "vertex"),
                    channel, (float)Number(op, "value", 0, 1000));
            case "setSurfaceMaterial":
                Fields(op, "op", "sector", "surface", "materialIndex");
                var materialIndex = Integer(op, "materialIndex");
                return new SetMaterialCommand(Surface(), At(doc.Materials, materialIndex, "material"), materialIndex);
            case "setSurfaceFlags":
                Fields(op, "op", "sector", "surface", "flags");
                return new SetSurfaceFlagsCommand(Surface(), UnsignedFlags(op, "flags"));
            case "setSectorFlags":
                Fields(op, "op", "sector", "flags");
                return new SetSectorFlagsCommand(Sector(), UnsignedFlags(op, "flags"));
            case "setSectorAmbient":
                Fields(op, "op", "sector", "value");
                float ambient = (float)Number(op, "value", 0, 1);
                return new SetSectorAmbientCommand(Sector(), new ColorF(ambient, ambient, ambient));
            case "setGravity":
                Fields(op, "op", "value");
                return new SetHeaderFieldCommand<float>(level.Header, "gravity", (float)Number(op, "value", 0, 10000), h => h.Gravity, (h, v) => h.Gravity = v);
            case "addBox":
                Fields(op, "op", "center", "halfSize");
                return new CreateSectorCommand(level, SectorFactory.CreateBox(level, Vector(op, "center"), Number(op, "halfSize", 0.0001, 100000), string.Empty, -1));
            case "addThing":
                Fields(op, "op", "sector", "template", "name", "position");
                var tpl = Token(op, "template");
                if (!level.Templates.ContainsKey(tpl)) throw new AgentRequestException("Template is not defined in this level.");
                return new CreateThingCommand(level, new Thing { Level = level, Sector = Sector(), Template = tpl,
                    Name = Token(op, "name"), Position = Vector(op, "position") });
            case "addLight":
                Fields(op, "op", "position", "rgb", "intensity", "range");
                return new CreateLightCommand(level, new Light { Position = Vector(op, "position"), Color = Color(op, "rgb"),
                    Intensity = Number(op, "intensity", 0, 1000), Range = Number(op, "range", 0.0001, 100000) });
            default: throw new AgentRequestException($"Unknown operation '{name}'. Use schema to list supported operations.");
        }
    }

    public static readonly IReadOnlyDictionary<string, string[]> OperationFields = new Dictionary<string, string[]>
    {
        ["setThingPosition"] = ["thing", "position", "sector"], ["setThingOrientation"] = ["thing", "orientation"],
        ["setThingName"] = ["thing", "name"], ["setThingTemplate"] = ["thing", "template"],
        ["setThingValue"] = ["thing", "key", "value"], ["setVertexPosition"] = ["sector", "vertex", "position"],
        ["clearThingArchitecture"] = ["thing"],
        ["setVertexLight"] = ["sector", "vertex", "channel", "value"],
        ["setSurfaceMaterial"] = ["sector", "surface", "materialIndex"], ["setSurfaceFlags"] = ["sector", "surface", "flags"],
        ["setSectorFlags"] = ["sector", "flags"], ["setSectorAmbient"] = ["sector", "value"],
        ["setGravity"] = ["value"], ["addBox"] = ["center", "halfSize"],
        ["addThing"] = ["sector", "template", "name", "position"], ["addLight"] = ["position", "rgb", "intensity", "range"],
    };

    private static void Fields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new AgentRequestException("Expected a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new AgentRequestException($"Duplicate or unsupported property '{property.Name}'.");
        foreach (var name in allowed)
            if (!names.Contains(name)) throw new AgentRequestException($"Missing required property '{name}'.");
    }
    private static string String(JsonElement value, string key) => value.GetProperty(key).ValueKind == JsonValueKind.String
        ? value.GetProperty(key).GetString()! : throw new AgentRequestException($"{key} must be a string.");
    private static string Token(JsonElement value, string key)
    {
        var text = String(value, key);
        if (text.Length is < 1 or > 127 || text.Any(c => c <= 32 || c >= 127 || c is '#' or '"'))
            throw new AgentRequestException($"{key} must be a nonempty ASCII token of at most 127 characters without whitespace, quotes, or comments.");
        return text;
    }
    private static int Integer(JsonElement value, string key) => value.GetProperty(key).TryGetInt32(out var i)
        ? i : throw new AgentRequestException($"{key} must be an integer.");
    private static long UnsignedFlags(JsonElement value, string key)
    {
        if (!value.GetProperty(key).TryGetInt64(out var i) || i < 0 || i > uint.MaxValue)
            throw new AgentRequestException($"{key} must be an unsigned 32-bit integer, expressed in decimal JSON.");
        return i;
    }
    private static double Number(JsonElement value, string key, double min, double max)
    {
        if (!value.GetProperty(key).TryGetDouble(out var number) || !double.IsFinite(number) || number < min || number > max)
            throw new AgentRequestException($"{key} must be finite and between {min} and {max}.");
        return number;
    }
    private static Vec3 Vector(JsonElement value, string key)
    {
        var a = value.GetProperty(key);
        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 3) throw new AgentRequestException($"{key} must contain three numbers.");
        var n = a.EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (n.Any(x => !double.IsFinite(x) || System.Math.Abs(x) > 1000000)) throw new AgentRequestException($"{key} contains an invalid or out-of-range coordinate.");
        return new Vec3(n[0], n[1], n[2]);
    }
    private static ColorF Color(JsonElement value, string key)
    {
        var v = Vector(value, key);
        if (v.X < 0 || v.Y < 0 || v.Z < 0 || v.X > 1 || v.Y > 1 || v.Z > 1) throw new AgentRequestException("RGB values must be between zero and one.");
        return new ColorF((float)v.X, (float)v.Y, (float)v.Z);
    }
    private static T At<T>(IReadOnlyList<T> list, int index, string kind) => (uint)index < (uint)list.Count
        ? list[index] : throw new AgentRequestException($"{kind} index {index} is outside the current document.");
    private static double[] Position(Vec3 p) => [p.X, p.Y, p.Z];
    private static object Counts(Level level) => new { sectors = level.Sectors.Count,
        surfaces = level.Sectors.Sum(s => s.Surfaces.Count), vertices = level.Sectors.Sum(s => s.Vertices.Count),
        things = level.Things.Count, lights = level.Lights.Count, cogs = level.Cogs.Count,
        architecturalObjects = level.ArchitecturalLighting.Count };
    private static object[] DescribeIssues(IEnumerable<ConsistencyIssue> issues) => issues.Select(i => (object)new
        { type = i.Type.ToString(), severity = i.Severity.ToString(), i.SectorIndex, i.SurfaceIndex, i.Message }).ToArray();
}

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
using Sed.Formats.Resources;

namespace Sed.Formats.Automation;

public sealed class AgentRequestException(string message, int? operationIndex = null,
    string? operation = null, string? jsonPointer = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int? OperationIndex { get; } = operationIndex;
    public string? Operation { get; } = operation;
    public string? JsonPointer { get; } = jsonPointer;
}
public sealed class AgentConflictException(string message) : Exception(message);
public sealed class AgentValidationException(string message, object issues) : Exception(message)
{
    public object Issues { get; } = issues;
}

public sealed record AgentInputDependency(string Role, string Path, bool Exists, long? Bytes, string? Sha256);
public sealed record AgentDocumentSnapshot(JklDocument Document, string InputSha256, string SourceRevision,
    IReadOnlyList<AgentInputDependency> Dependencies);
public sealed record PreparedAgentEdit(string InputPath, string InputSha256, string OutputText,
    object Report)
{
    public string? SourceRevision { get; init; }
    public IReadOnlyList<AgentInputDependency> Dependencies { get; init; } = Array.Empty<AgentInputDependency>();
    internal AgentResourceCapture? ResourceCapture { get; init; }
    public string? ResourceRevision => ResourceCapture?.Revision;
}

/// <summary>
/// Headless document operations. Edits use the GUI's Core commands on an isolated
/// parsed document. Planning never writes; committing creates a new file atomically.
/// Numeric selectors are valid only for the exact input hash in the request.
/// </summary>
public static partial class AgentWorkspace
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
        var snapshot = LoadWithProvenance(path);
        return (snapshot.Document, snapshot.InputSha256);
    }

    // Describes the extension captured while parsing this document's input, not
    // edits to its model or an audit of preservation history/runtime compatibility.
    internal static object DescribeSourceLightMetadata(JklDocument document)
    {
        bool validated = document.OriginalSourceLightTextHash is not null;
        return new
        {
            state = validated ? "validated" : "absent",
            binding = validated ? "ordinary-lights-by-index" : null,
        };
    }

    public static AgentDocumentSnapshot LoadWithProvenance(string path)
    {
        path = Path.GetFullPath(path);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".jkl" or ".jed" or ".sed" or ".ned"))
            throw new AgentRequestException("Read a JKL or supported text JED/SED/NED project. Use archive/extract for GOB/GOO contents.");
        var bytes = ReadBoundedFile(path);
        string hash = Hash(bytes);
        if (extension != ".jkl")
        {
            string text = LegacyText.Decode(bytes);
            bool mots = JedImporter.DetectProjectType(text) == ProjectType.MysteriesOfTheSith;
            string library = mots ? "mots.tpl" : "master.tpl";
            var captured = new[] {
                CaptureDependency("bundledTemplates", Path.Combine(AppContext.BaseDirectory, "data", library)),
                CaptureDependency("companionTemplates", Path.Combine(Path.GetDirectoryName(path)!, library)) };
            var dependencies = Array.AsReadOnly(captured.Select(c => c.metadata).ToArray());
            var templates = JedImporter.LoadTemplateTexts(captured.Where(c => c.content is not null).Select(c => LegacyText.Decode(c.content!)));
            var import = JedImporter.ImportText(text, templates);
            EnsureInputsUnchanged(path, hash, dependencies);
            if (templates.Count == 0 && import.Document.Level.Things.Count > 0)
                import.Document.PreservationWarnings.Add($"No {library} template library was found beside the project or in the editor data folder; supply templates before exporting game entities.");
            if (!ReferenceEquals(import.Document.PreservationWarnings, import.Warnings))
                import.Document.PreservationWarnings.AddRange(import.Warnings);
            return new(import.Document, hash, ComputeSourceRevision(hash, dependencies), dependencies);
        }
        return new(JklParser.ParseDocument(LegacyText.Decode(bytes)), hash, hash, Array.Empty<AgentInputDependency>());
    }

    private static byte[] ReadBoundedFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxLevelBytes) throw new AgentRequestException("The source or template library exceeds the 128 MiB input limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new AgentConflictException("An input grew while it was being captured.");
        return bytes;
    }

    private static (AgentInputDependency metadata, byte[]? content) CaptureDependency(string role, string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) return (new(role, path, false, null, null), null);
        byte[] content = ReadBoundedFile(path);
        return (new(role, path, true, content.LongLength, Hash(content)), content);
    }

    private static string ComputeSourceRevision(string inputHash, IReadOnlyList<AgentInputDependency> dependencies)
    {
        if (dependencies.Count == 0) return inputHash;
        // Paths are reported for review; content/role/existence bind semantics independently of machine locations.
        var canonical = new StringBuilder("sed-agent-native-source-v1\n").Append(inputHash).Append('\n');
        foreach (var dependency in dependencies.OrderBy(d => d.Role, StringComparer.Ordinal))
            canonical.Append(dependency.Role).Append('|').Append(Path.GetFileName(dependency.Path).ToLowerInvariant())
                .Append('|').Append(dependency.Exists ? "present" : "absent").Append('|')
                .Append(dependency.Sha256 ?? "-").Append('\n');
        return Hash(Encoding.UTF8.GetBytes(canonical.ToString()));
    }

    private static void EnsureInputsUnchanged(string path, string expectedHash, IReadOnlyList<AgentInputDependency> dependencies)
    {
        if (Hash(ReadBoundedFile(path)) != expectedHash)
            throw new AgentConflictException("The input changed after capture. No output was written.");
        foreach (var dependency in dependencies)
        {
            var current = CaptureDependency(dependency.Role, dependency.Path).metadata;
            if (current.Exists != dependency.Exists || current.Bytes != dependency.Bytes || current.Sha256 != dependency.Sha256)
                throw new AgentConflictException($"The {dependency.Role} input changed after capture: {dependency.Path}. No output was written.");
        }
    }

    public static object Inspect(string path, int? sectorIndex = null)
    {
        var snapshot = LoadWithProvenance(path);
        var doc = snapshot.Document; var hash = snapshot.InputSha256;
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
            schemaVersion = SchemaVersion, inputSha256 = hash, sourceRevision = snapshot.SourceRevision,
            dependencies = snapshot.Dependencies, game = level.Kind.ToString(),
            counts = Counts(level), materials = doc.Materials,
            sourceLightMetadata = DescribeSourceLightMetadata(doc),
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

    public static object Validate(string path, string? resourceRequestText = null)
    {
        var snapshot = LoadWithProvenance(path);
        var doc = snapshot.Document; var hash = snapshot.InputSha256;
        var options = ParseExportResources(resourceRequestText, snapshot);
        using var resources = options is null ? null : new ExplicitArchiveResources(options.Archives);
        var architecture = ArchitectureResourceValidation.Evaluate(doc.Level, resources);
        CheckResourceRevision(options, architecture.ResourceRevision);
        resources?.VerifyUnchanged();
        EnsureInputsUnchanged(path, hash, snapshot.Dependencies);
        var issues = ConsistencyChecker.Check(doc.Level);
        return new { schemaVersion = SchemaVersion, inputSha256 = hash, sourceRevision = snapshot.SourceRevision,
            dependencies = snapshot.Dependencies,
            valid = issues.All(i => i.Severity != IssueSeverity.Error) && !architecture.HasErrors, issues = DescribeIssues(issues),
            resourceRevision = architecture.ResourceRevision, architectureValidation = architecture,
            sourceLightMetadata = DescribeSourceLightMetadata(doc),
            preservationWarnings = doc.PreservationWarnings };
    }

    public static PreparedAgentEdit ConvertDocument(string path, string? resourceRequestText = null)
    {
        var snapshot = LoadWithProvenance(path);
        var doc = snapshot.Document; var hash = snapshot.InputSha256;
        var options = ParseExportResources(resourceRequestText, snapshot);
        using var resources = options is null ? null : new ExplicitArchiveResources(options.Archives);
        var exported = ResourceValidatedExport.Prepare(doc, resources);
        string output = exported.OutputText;
        CheckResourceRevision(options, exported.Validation.ResourceRevision);
        var reopened = JklParser.ParseDocument(output);
        var issues = ConsistencyChecker.Check(reopened.Level);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            throw new AgentValidationException("Converted output has validation errors. Inspect the project before exporting.", DescribeIssues(issues));
        return new PreparedAgentEdit(Path.GetFullPath(path), hash, output, new
        {
            schemaVersion = 1, inputSha256 = hash, sourceRevision = snapshot.SourceRevision,
            dependencies = snapshot.Dependencies, outputSha256 = Hash(Encoding.UTF8.GetBytes(output)), counts = Counts(reopened.Level),
            resourceRevision = exported.Validation.ResourceRevision, architectureValidation = exported.Validation,
            sourceLightMetadata = DescribeSourceLightMetadata(doc),
            outputSourceLightMetadata = DescribeSourceLightMetadata(reopened),
            preservationWarnings = doc.PreservationWarnings, issues = DescribeIssues(issues),
            note = "Legacy conversion uses the recorded importer coverage. Editor-only and unsupported records can produce warnings; this is not a gameplay test.",
        }) { SourceRevision = snapshot.SourceRevision, Dependencies = snapshot.Dependencies,
            ResourceCapture = CaptureResources(options, exported.Validation, output) };
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
        FieldsWithOptional(root, ["sourceRevision", "resourceArchives", "resourceRevision"], "schemaVersion", "inputSha256", "operations");
        if (Integer(root, "schemaVersion") != SchemaVersion) throw new AgentRequestException("Unsupported patch schemaVersion.");
        var expected = String(root, "inputSha256");
        if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
            throw new AgentRequestException("inputSha256 must be a 64-digit SHA256 from inspect.");
        var snapshot = LoadWithProvenance(path);
        var doc = snapshot.Document; var hash = snapshot.InputSha256;
        if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException("The input changed since inspection. Inspect the current file and regenerate the patch.");
        if (!root.TryGetProperty("sourceRevision", out _) && snapshot.Dependencies.Count != 0)
            throw new AgentRequestException("Native projects require sourceRevision from inspect/query so companion templates are included in the input guard.", jsonPointer: "/sourceRevision");
        if (root.TryGetProperty("sourceRevision", out _))
        {
            var revision = String(root, "sourceRevision");
            if (revision.Length != 64 || revision.Any(c => !Uri.IsHexDigit(c)))
                throw new AgentRequestException("sourceRevision must be a 64-digit SHA256 from inspect/query.", jsonPointer: "/sourceRevision");
            if (!string.Equals(revision, snapshot.SourceRevision, StringComparison.OrdinalIgnoreCase))
                throw new AgentConflictException("The source or a native template dependency changed since inspection. Inspect again and regenerate the patch.");
        }
        var operations = root.GetProperty("operations");
        if (operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > MaxOperations)
            throw new AgentRequestException("operations must contain between 1 and 1000 operations.");
        var resourceOptions = ParseResourceOptions(root);
        using var resources = resourceOptions is null ? null : new ExplicitArchiveResources(resourceOptions.Archives);

        var before = Counts(doc.Level);
        var history = new EditHistory();
        var changes = new List<(JsonElement operation, int? createdIndex, string command, object? before, object? after)>();
        int operationIndex = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            // Build each command against the current isolated model so later
            // operations can address objects appended by earlier operations.
            string? name = operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("op", out var operationName)
                && operationName.ValueKind == JsonValueKind.String ? operationName.GetString() : null;
            try
            {
                var command = PrepareOperation(doc, operation);
                int? created = name switch { "addBox" or "extrudeSurface" => doc.Level.Sectors.Count, "addThing" => doc.Level.Things.Count,
                    "registerMaterial" => doc.Materials.Count, "registerColormap" => doc.Level.ColorMaps.Count,
                    "addLight" => doc.Level.Lights.Count, _ => null };
                object? oldValue = created is null || name == "extrudeSurface" ? OperationEffect(doc, operation, null) : null;
                history.Do(command);
                changes.Add((operation, created, command.Name, oldValue, OperationEffect(doc, operation, created)));
            }
            catch (Exception error) when (error is AgentRequestException or ArgumentException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
            {
                string pointer = $"/operations/{operationIndex}" + (error is AgentRequestException requestError ? requestError.JsonPointer : null);
                throw new AgentRequestException(error.Message, operationIndex, name, pointer, error);
            }
            operationIndex++;
        }
        var issues = ConsistencyChecker.Check(doc.Level);
        if (issues.Any(i => i.Severity == IssueSeverity.Error))
            throw new AgentValidationException("The proposed edits do not pass level validation. No file was written.", DescribeIssues(issues));
        var exported = ResourceValidatedExport.Prepare(doc, resources);
        var text = exported.OutputText;
        CheckResourceRevision(resourceOptions, exported.Validation.ResourceRevision);
        var reopened = JklParser.ParseDocument(text);
        var roundTripIssues = ConsistencyChecker.Check(reopened.Level);
        if (roundTripIssues.Any(i => i.Severity == IssueSeverity.Error))
            throw new AgentValidationException("The serialized level does not pass validation. No file was written.", DescribeIssues(roundTripIssues));
        if (reopened.Level.Sectors.Count != doc.Level.Sectors.Count || reopened.Level.Things.Count != doc.Level.Things.Count)
            throw new AgentValidationException("The serialized level changed its sector or thing count.", Array.Empty<object>());
        var effects = new List<object>();
        for (int index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            object finalExpected = OperationEffect(doc, change.operation, change.createdIndex);
            object readBack = OperationEffect(reopened, change.operation, change.createdIndex);
            if (JsonSerializer.Serialize(finalExpected, JsonOptions) != JsonSerializer.Serialize(readBack, JsonOptions))
                throw new AgentValidationException($"Operation {index} ({String(change.operation, "op")}) has an effect that cannot be preserved in the serialized level. No file was written.",
                    new[] { new { operationIndex = index, operation = String(change.operation, "op"),
                        jsonPointer = $"/operations/{index}", expected = finalExpected, actual = readBack } });
            effects.Add(new { index, operation = String(change.operation, "op"), command = change.command,
                before = change.before, afterOperation = change.after, serializedFinal = readBack });
        }
        var outputHash = Hash(new UTF8Encoding(false).GetBytes(text));
        return new PreparedAgentEdit(Path.GetFullPath(path), hash, text, new
        {
            schemaVersion = SchemaVersion, inputSha256 = hash, sourceRevision = snapshot.SourceRevision,
            dependencies = snapshot.Dependencies, outputSha256 = outputHash,
            resourceRevision = exported.Validation.ResourceRevision, architectureValidation = exported.Validation,
            sourceLightMetadata = DescribeSourceLightMetadata(doc),
            outputSourceLightMetadata = DescribeSourceLightMetadata(reopened),
            before, after = Counts(reopened.Level), changes = effects, issues = DescribeIssues(roundTripIssues),
            preservationWarnings = doc.PreservationWarnings,
            note = "Planning uses the GUI's Core commands. Explicit archives add current all-LOD architectural model validation; missing historical baselines remain unknown. Other asset closure and gameplay are not tested. Moved models may require relighting.",
        }) { SourceRevision = snapshot.SourceRevision, Dependencies = snapshot.Dependencies,
            ResourceCapture = CaptureResources(resourceOptions, exported.Validation, text) };
    }

    public static object Commit(PreparedAgentEdit edit, string outputPath)
    {
        // Output creation is deliberately no-clobber. An agent can produce a
        // reviewable result without overwriting the user's original project.
        EnsureInputsUnchanged(edit.InputPath, edit.InputSha256, edit.Dependencies);
        using var resources = ReopenCapturedResources(edit);
        WriteNewBytes(outputPath, new UTF8Encoding(false).GetBytes(edit.OutputText), () => {
            EnsureInputsUnchanged(edit.InputPath, edit.InputSha256, edit.Dependencies);
            resources?.VerifyUnchanged();
        });
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

    private static void WriteNewBytes(string path, byte[] bytes, Action? beforePromote = null)
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
            beforePromote?.Invoke();
            File.Move(temporary, path, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static IEditCommand PrepareOperation(JklDocument doc, JsonElement op)
    {
        if (TryPrepareLightOperation(doc, op, out var lightCommand)) return lightCommand!;
        if (TryPrepareMaterialOperation(doc, op, out var materialCommand)) return materialCommand!;
        if (TryPrepareGeometryOperation(doc, op, out var geometryCommand)) return geometryCommand!;
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
                long flags = UnsignedFlags(op, "flags");
                if (((flags & 0x1000) != 0) != Sector().HasCollideBox)
                    throw new AgentRequestException("The COLLIDEBOX flag must agree with stored collision-box data; this operation cannot create or remove a collision box.", jsonPointer: "/flags");
                return new SetSectorFlagsCommand(Sector(), flags);
            case "setSectorAmbient":
                Fields(op, "op", "sector", "value");
                float ambient = (float)Number(op, "value", 0, 1);
                return new SetSectorAmbientCommand(Sector(), new ColorF(ambient, ambient, ambient));
            case "setGravity":
                Fields(op, "op", "value");
                return new SetHeaderFieldCommand<float>(level.Header, "gravity", (float)Number(op, "value", 0, 10000), h => h.Gravity, (h, v) => h.Gravity = v);
            case "addBox":
                Fields(op, "op", "center", "halfSize");
                EnsureKnownGeometrySections(doc);
                return new CreateSectorCommand(level, SectorFactory.CreateBox(level, Vector(op, "center"), Number(op, "halfSize", 0.0001, 100000), string.Empty, -1));
            case "addThing":
                Fields(op, "op", "sector", "template", "name", "position");
                var tpl = Token(op, "template");
                if (!level.Templates.ContainsKey(tpl)) throw new AgentRequestException("Template is not defined in this level.");
                return new CreateThingCommand(level, new Thing { Level = level, Sector = Sector(), Template = tpl,
                    Name = Token(op, "name"), Position = Vector(op, "position") });
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
        ["setLight"] = ["light"],
        ["registerMaterial"] = ["name"], ["registerColormap"] = ["name"], ["setSectorColormap"] = ["sector", "colormapIndex"],
        ["fitSurfaceTexture"] = ["sector", "surface", "width", "height"], ["setSurfaceUv"] = ["sector", "surface", "uv"],
        ["setSurfaceRenderModes"] = ["sector", "surface", "geometryMode", "lightMode", "textureMode"],
        ["extrudeSurface"] = ["sector", "surface", "distance"], ["adjoinSurfaces"] = ["sector", "surface", "otherSector", "otherSurface"],
    };

    // Reports the editable fields addressed by one operation. The final readback
    // is compared with the final model, so a later operation may intentionally
    // replace an earlier value without producing a false serialization failure.
    private static object OperationEffect(JklDocument doc, JsonElement op, int? created)
    {
        string name = String(op, "op");
        var level = doc.Level;
        if (name is "registerMaterial" or "registerColormap" or "setSectorColormap" or "fitSurfaceTexture" or "setSurfaceUv" or "setSurfaceRenderModes")
            return MaterialOperationEffect(doc, op, created);
        if (name is "extrudeSurface" or "adjoinSurfaces") return GeometryOperationEffect(doc, op, created);
        if (name == "setGravity") return new { target = new { kind = "header" }, state = new { gravity = level.Header.Gravity } };
        if (name.StartsWith("setThing", StringComparison.Ordinal) || name is "addThing" or "clearThingArchitecture")
        {
            int index = created ?? Integer(op, "thing"); var thing = At(level.Things, index, "thing");
            return new { target = new { kind = "thing", index }, state = new {
                thing.Name, thing.Template, position = Position(thing.Position), orientation = new[] { thing.Pitch, thing.Yaw, thing.Roll },
                sector = thing.Sector is null ? -1 : level.Sectors.IndexOf(thing.Sector), thing.ArchLightingIndex,
                values = thing.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { key = p.Key, value = p.Value }).ToArray() } };
        }
        if (name is "addLight" or "setLight") return LightOperationEffect(doc, op, created);
        int sectorIndex = created ?? Integer(op, "sector"); var sector = At(level.Sectors, sectorIndex, "sector");
        if (name == "addBox") return new { target = new { kind = "sector", index = sectorIndex }, state = new {
            vertices = sector.Vertices.Select(v => Position(v.Position)).ToArray(), surfaces = sector.Surfaces.Count } };
        if (name == "setSectorFlags") return new { target = new { kind = "sector", index = sectorIndex }, state = new { sector.Flags, sector.HasCollideBox } };
        if (name == "setSectorAmbient") return new { target = new { kind = "sector", index = sectorIndex }, state = new {
            ambient = new[] { sector.Ambient.R, sector.Ambient.G, sector.Ambient.B } } };
        if (name.StartsWith("setSurface", StringComparison.Ordinal))
        {
            int index = Integer(op, "surface"); var surface = At(sector.Surfaces, index, "surface");
            return new { target = new { kind = "surface", sector = sectorIndex, index }, state = new { surface.Material, surface.MaterialIndex, surface.SurfFlags } };
        }
        int vertexIndex = Integer(op, "vertex"); var vertex = At(sector.Vertices, vertexIndex, "vertex");
        if (name == "setVertexPosition") return new { target = new { kind = "vertex", sector = sectorIndex, index = vertexIndex }, state = new { position = Position(vertex.Position) } };
        return new { target = new { kind = "vertex", sector = sectorIndex, index = vertexIndex }, state = new {
            corners = sector.Surfaces.SelectMany((surface, surfaceIndex) => surface.Corners.Select((corner, cornerIndex) =>
                new { surfaceIndex, cornerIndex, corner })).Where(c => c.corner.Vertex == vertex).Select(c => new {
                    surface = c.surfaceIndex, corner = c.cornerIndex, mono = c.corner.MonoIntensity ?? c.corner.Intensity.R,
                    rgb = new[] { c.corner.Intensity.R, c.corner.Intensity.G, c.corner.Intensity.B } }).ToArray() } };
    }

    private static void Fields(JsonElement value, params string[] allowed)
        => FieldsWithOptional(value, Array.Empty<string>(), allowed);

    private static void FieldsWithOptional(JsonElement value, string[] optional, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new AgentRequestException("Expected a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || !(required.Contains(property.Name, StringComparer.Ordinal) || optional.Contains(property.Name, StringComparer.Ordinal)))
                throw FieldError($"Duplicate or unsupported property '{property.Name}'.", property.Name);
        foreach (var name in required)
            if (!names.Contains(name)) throw FieldError($"Missing required property '{name}'.", name);
    }
    private static AgentRequestException FieldError(string message, string key) =>
        new(message, jsonPointer: "/" + key.Replace("~", "~0").Replace("/", "~1"));
    private static string String(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString()! : throw FieldError($"{key} must be a string.", key);
    private static string Token(JsonElement value, string key)
    {
        var text = String(value, key);
        if (text.Length is < 1 or > 127 || text.Any(c => c <= 32 || c >= 127 || c is '#' or '"'))
            throw FieldError($"{key} must be a nonempty ASCII token of at most 127 characters without whitespace, quotes, or comments.", key);
        return text;
    }
    private static int Integer(JsonElement value, string key) => value.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var i)
        ? i : throw FieldError($"{key} must be an integer.", key);
    private static long UnsignedFlags(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var i) || i < 0 || i > uint.MaxValue)
            throw FieldError($"{key} must be an unsigned 32-bit integer, expressed in decimal JSON.", key);
        return i;
    }
    private static double Number(JsonElement value, string key, double min, double max)
    {
        if (!value.TryGetProperty(key, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var number) || !double.IsFinite(number) || number < min || number > max)
            throw FieldError($"{key} must be finite and between {min} and {max}.", key);
        return number;
    }
    private static Vec3 Vector(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var a) || a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 3)
            throw FieldError($"{key} must contain three numbers.", key);
        var n = new double[3]; int i = 0;
        foreach (var element in a.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out n[i]) || !double.IsFinite(n[i]) || System.Math.Abs(n[i]) > 1000000)
                throw new AgentRequestException($"{key} contains an invalid or out-of-range coordinate.", jsonPointer: $"/{key}/{i}");
            i++;
        }
        return new Vec3(n[0], n[1], n[2]);
    }
    private static ColorF Color(JsonElement value, string key)
    {
        var v = Vector(value, key);
        if (v.X < 0 || v.Y < 0 || v.Z < 0 || v.X > 1 || v.Y > 1 || v.Z > 1) throw FieldError("RGB values must be between zero and one.", key);
        return new ColorF((float)v.X, (float)v.Y, (float)v.Z);
    }
    private static T At<T>(IReadOnlyList<T> list, int index, string kind) => (uint)index < (uint)list.Count
        ? list[index] : throw FieldError($"{kind} index {index} is outside the current document.", kind == "material" ? "materialIndex" : kind);
    private static double[] Position(Vec3 p) => [p.X, p.Y, p.Z];
    private static object Counts(Level level) => new { sectors = level.Sectors.Count,
        surfaces = level.Sectors.Sum(s => s.Surfaces.Count), vertices = level.Sectors.Sum(s => s.Vertices.Count),
        things = level.Things.Count, lights = level.Lights.Count, cogs = level.Cogs.Count,
        architecturalObjects = level.ArchitecturalLighting.Count };
    private static object[] DescribeIssues(IEnumerable<ConsistencyIssue> issues) => issues.Select(i => (object)new
        { type = i.Type.ToString(), severity = i.Severity.ToString(), i.SectorIndex, i.SurfaceIndex, i.ItemIndex, i.ParameterIndex, i.Message }).ToArray();
}

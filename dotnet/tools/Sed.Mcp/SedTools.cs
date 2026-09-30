using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;
using Sed.Formats.Automation;
using Sed.Formats.Resources;
using Sed.Mcp.Infrastructure;

namespace Sed.Mcp;

/// <summary>Transport adaptation only. Domain parsing, geometry and writes remain shared with the CLI.</summary>
public sealed class SedTools : IDisposable
{
    public const int MaxResultBytes = 2 * 1024 * 1024;
    public const long MaxInputBytes = 16L * 1024 * 1024;
    public const long MaxArchiveBytes = 512L * 1024 * 1024;
    public const int MaxPatchOperations = 128;
    private readonly McpFilePolicy policy;
    private readonly OperationStore operations;
    private readonly SnapshotWorker snapshot;
    private readonly SemaphoreSlim admission = new(1, 1);
    private readonly SemaphoreSlim observationAdmission = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(ToolCatalog.Json)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public SedTools(HostOptions options)
    {
        policy = new(options.Workspace, options.ResourceRoots, Path.Combine(AppContext.BaseDirectory, "data"));
        operations = new(policy);
        snapshot = new(options.AgentPath, options.AgentVersion);
    }

    public async ValueTask<CallToolResult> CallAsync(string name, IDictionary<string, JsonElement>? arguments,
        CancellationToken token = default)
    {
        bool entered = false;
        try
        {
            var args = arguments ?? new Dictionary<string, JsonElement>();
            ValidateArguments(name, args);
            token.ThrowIfCancellationRequested();
            // Reconciliation and discovery do not queue behind a long synchronous domain call.
            if (name == "sed_outcome")
            {
                if (!await observationAdmission.WaitAsync(0, token)) return Failure("busy", "An outcome observation is already active; retry later.");
                try { return await Task.Run(() => { token.ThrowIfCancellationRequested(); return Success(new { outcome = operations.Get(Text(args, "operationId")) }); }, CancellationToken.None); }
                finally { observationAdmission.Release(); }
            }
            if (name == "sed_discover") return Success(Discover(OptionalText(args, "topic")));
            if (!await admission.WaitAsync(0, token)) return Failure("busy", "One operation is already active. Retry after it finishes; no work was queued.");
            entered = true;
            if (name == "sed_snapshot")
            {
                string level = Level(args), request = Request(args)!;
                if (!Path.GetExtension(level).Equals(".jkl", StringComparison.OrdinalIgnoreCase))
                    throw new AgentRequestException("MCP snapshots require JKL. Plan and save a reviewed native conversion first; the isolated worker must not discover a different template library.");
                var parsed = AgentSnapshotRequest.Parse(request);
                if (parsed.Width * (long)parsed.Height > 2_097_152) throw new AgentRequestException("Snapshot exceeds the pixel budget.");
                string output = policy.WriteOutput(Text(args, "output"), ".png");
                var result = await snapshot.RunAsync(level, request, output, token);
                SnapshotEvidence.Validate(result, request);
                return SnapshotSuccess(result);
            }
            // Keep transport cancellation responsive. Synchronous domain work remains admitted until
            // it really finishes. Cancellation is checked before promotion; no pretend hard timeout.
            return await Task.Run(() => Dispatch(name, args, token), CancellationToken.None);
        }
        catch (SnapshotWorkerException e) { return SnapshotFailure(e); }
        catch (AgentConflictException e) { return Failure("conflict", e.Message, new { e.OperationIndex, e.Operation, e.JsonPointer }); }
        catch (AgentValidationException e) { return Failure("validation_failed", e.Message, e.Issues); }
        catch (OperationCanceledException)
        {
            return Failure("cancelled", name == "sed_snapshot"
                ? "Preview cancelled. Previews have no durable outcome record. Inspect the intended PNG path with authorized file/image tools before another preview; never retry blindly or assume cancellation means no file exists."
                : "Request cancelled. For a level commit, query sed_outcome with its operationId; cancellation does not imply rollback.");
        }
        catch (AgentRequestException e) { return Failure("invalid_request", e.Message, new { e.OperationIndex, e.Operation, e.JsonPointer }); }
        catch (JsonException e) { return Failure("invalid_request", e.Message, new { e.Path }); }
        catch (UnauthorizedAccessException e) { return Failure("path_denied", e.Message); }
        catch (IOException e) { return Failure("io_error", e.Message); }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or NotSupportedException or OverflowException or KeyNotFoundException)
        { return Failure("invalid_request", e.Message); }
        finally { if (entered) admission.Release(); }
    }

    private CallToolResult Dispatch(string name, IDictionary<string, JsonElement> args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (name == "sed_new")
        {
            string game = Text(args, "game");
            bool write = IsWrite(args);
            if (write && Existing(name, args) is { } old) return old;
            policy.CheckBundledTemplates();
            var prepared = AgentWorkspace.PrepareNew(game);
            if (!write) return Success(prepared.Report);
            string expected = Expected(args);
            CheckExpected(prepared.OutputText, expected);
            string output = policy.WriteOutput(Text(args, "output"), ".jkl");
            var intent = new OperationIntent(name, null, null, null, output, RequestHash(name, args), expected);
            return Commit(Text(args, "operationId"), intent, () => {
                policy.CheckBundledTemplates();
                policy.WriteOutput(output, ".jkl");
                return AgentWorkspace.Create(output, game, expected, token);
            }, token);
        }
        if (name == "sed_archive")
        {
            string path = policy.ReadResource(Text(args, "archive"));
            CheckLength(path, MaxArchiveBytes);
            return Success(AgentWorkspace.InspectArchive(path));
        }
        bool commits = name == "sed_apply" || name == "sed_convert" && IsWrite(args);
        if (commits && Existing(name, args) is { } existing) return existing;
        string level = Level(args);
        string? request = Request(args, required: name is not ("sed_convert" or "sed_diagnose"));
        switch (name)
        {
            case "sed_query": return Success(AgentWorkspace.Query(level, request!));
            case "sed_select": return Success(AgentWorkspace.Select(level, request!));
            case "sed_diagnose":
                string kind = Text(args, "kind");
                object diagnosed = kind switch {
                    "validate" => AgentWorkspace.Validate(level, request),
                    "assets" when request is not null => AgentWorkspace.DiagnoseAssets(level, request),
                    "lighting" when request is not null => AgentWorkspace.DiagnoseLighting(level, request),
                    "vertexColors" when request is not null => AgentWorkspace.DiagnoseVertexColors(level, request),
                    "bindings" when request is not null => AgentWorkspace.Bindings(level, request),
                    _ => throw new AgentRequestException("kind must be validate/assets/lighting/vertexColors/bindings; only validate may omit request.") };
                var checkedResult = BoundedElement(diagnosed);
                return kind == "validate" && checkedResult.TryGetProperty("valid", out var valid) && !valid.GetBoolean()
                    ? Failure("validation_failed", "Level diagnostics found errors.", checkedResult) : Success(checkedResult);
            case "sed_plan": case "sed_apply": case "sed_convert":
                var edit = name == "sed_convert" ? AgentWorkspace.ConvertDocument(level, request) : AgentWorkspace.Plan(level, request!);
                if (!commits) return Success(edit.Report);
                if (name == "sed_convert")
                {
                    // Normalization can hide input/template changes in identical output bytes.
                    // Bind the reviewed source as well as the reviewed serialized output.
                    string inputHash = ResourceManifest.NormalizeHash(Text(args, "expectedInputSha256"), "expectedInputSha256");
                    string sourceRevision = ResourceManifest.NormalizeHash(Text(args, "expectedSourceRevision"), "expectedSourceRevision");
                    if (edit.InputSha256 != inputHash || edit.SourceRevision != sourceRevision)
                        throw new AgentConflictException("Conversion source differs from the reviewed plan. Replan before saving.");
                }
                string expected = Expected(args);
                CheckExpected(edit.OutputText, expected);
                if (edit.ResourceRevision is not null && (request is null ||
                    !JsonNode.Parse(request)!.AsObject().TryGetPropertyValue("resourceRevision", out var revision) ||
                    !string.Equals(revision?.GetValue<string>(), edit.ResourceRevision, StringComparison.OrdinalIgnoreCase)))
                    throw new AgentConflictException("Resource-consuming commits require resourceRevision from the exact reviewed plan.");
                string output = policy.WriteOutput(Text(args, "output"), ".jkl");
                // Bound the response before promotion, so an oversized report cannot hide a successful write.
                _ = BoundedElement(edit.Report, MaxResultBytes - 128 * 1024);
                var intent = new OperationIntent(name, level, edit.InputSha256, edit.SourceRevision, output, RequestHash(name, args), expected);
                return Commit(Text(args, "operationId"), intent, () => {
                    policy.ReadLevel(level);
                    policy.WriteOutput(output, ".jkl");
                    if (request is not null)
                    { using var capturedRequest = JsonDocument.Parse(request); CheckResourcePaths(capturedRequest.RootElement); }
                    return AgentWorkspace.Commit(edit, output, expected, token);
                }, token);
            default: throw new AgentRequestException("Unknown SED tool.");
        }
    }

    private CallToolResult Commit(string id, OperationIntent intent, Func<object> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var begun = operations.Begin(id, intent);
        if (!begun.Created) return Success(new { replayed = true, outcome = operations.Get(id) });
        bool promoted = false;
        try
        {
            var result = action();
            promoted = true;
            operations.Complete(id);
            return Success(new { operationId = begun.Receipt.OperationId, result, outcome = operations.Get(id) });
        }
        catch (Exception error)
        {
            try
            {
                if (promoted || error is IOException)
                    operations.MarkUnknown(id, "Completion uncertain. Inspect output identity; do not automatically replay. " + Short(error.Message));
                else operations.Fail(id, Short(error.Message));
            }
            catch (Exception journalError) when (journalError is IOException or UnauthorizedAccessException or AgentConflictException)
            { return Failure("outcome_unknown", "Operation outcome could not be recorded. Query sed_outcome before further edits.", new { operationId = id, promoted, detail = Short(journalError.Message) }); }
            throw;
        }
    }

    private CallToolResult? Existing(string command, IDictionary<string, JsonElement> args)
    {
        var observation = operations.Get(Text(args, "operationId"));
        if (observation is null) return null;
        if (observation.Receipt.Intent.Command != command ||
            !string.Equals(observation.Receipt.Intent.RequestSha256, RequestHash(command, args), StringComparison.Ordinal))
            throw new AgentConflictException("operationId is already bound to another request. Inspect sed_outcome; no work was replayed.");
        return Success(new { replayed = true, outcome = observation });
    }

    private string Level(IDictionary<string, JsonElement> args)
    {
        string path = policy.ReadLevel(Text(args, "level"));
        CheckLength(path, MaxInputBytes);
        return path;
    }

    private string? Request(IDictionary<string, JsonElement> args, bool required = true)
    {
        if (!args.TryGetValue("request", out var request))
        { if (required) throw new AgentRequestException("request is required."); return null; }
        if (request.ValueKind != JsonValueKind.Object) throw new AgentRequestException("request must be an object.");
        string text = request.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > AgentWorkspace.MaxRequestBytes) throw new AgentRequestException("Domain request exceeds 1 MiB.");
        CheckResourcePaths(request);
        if (request.TryGetProperty("operations", out var edits) && edits.ValueKind == JsonValueKind.Array && edits.GetArrayLength() > MaxPatchOperations)
            throw new AgentRequestException("MCP limits a single patch to 128 operations. Split and replan larger jobs.");
        return text;
    }

    private void CheckResourcePaths(JsonElement request)
    {
        if (!request.TryGetProperty("resourceArchives", out var resources)) return;
        long total = 0;
        foreach (var archive in ResourceManifest.Parse(resources))
        {
            string path = policy.ReadResource(archive.Path);
            CheckLength(path, MaxArchiveBytes);
            total = checked(total + new FileInfo(path).Length);
            if (total > 4 * MaxArchiveBytes) throw new AgentRequestException("Resource manifest exceeds 2 GiB total input.");
        }
    }

    private static void CheckLength(string path, long max)
    { if (new FileInfo(path).Length > max) throw new AgentRequestException($"Input exceeds this adapter's {max}-byte limit."); }
    private static string Text(IDictionary<string, JsonElement> args, string key)
        => OptionalText(args, key) ?? throw new AgentRequestException($"{key} is required.");
    private static string? OptionalText(IDictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > 8192)
            throw new AgentRequestException($"{key} must be a nonempty string of at most 8192 characters.");
        return value.GetString();
    }
    private static string Expected(IDictionary<string, JsonElement> args) => ResourceManifest.NormalizeHash(Text(args, "expectedOutputSha256"), "expectedOutputSha256");
    private static void CheckExpected(string text, string expected)
    { if (AgentWorkspace.Hash(Encoding.UTF8.GetBytes(text)) != expected) throw new AgentConflictException("Output differs from the reviewed plan. Replan before applying."); }
    private static bool IsWrite(IDictionary<string, JsonElement> args)
    {
        int count = new[] { "output", "operationId", "expectedOutputSha256" }.Count(args.ContainsKey);
        if (count is not (0 or 3)) throw new AgentRequestException("A commit requires output, operationId and expectedOutputSha256 together; omit all three to plan.");
        return count == 3;
    }
    private static string Short(string text) => text.Length > 2000 ? text[..2000] : text;

    private static object Discover(string? topic) => topic switch {
        null => new { schemaVersion = 1, adapter = "sed-mcp", version = HostOptions.Version,
            tools = ToolCatalog.Tools.Select(t => t.Name), operations = AgentWorkspace.OperationFields.Keys,
            limits = new { inputBytes = MaxInputBytes, domainRequestBytes = AgentWorkspace.MaxRequestBytes,
                resultBytes = MaxResultBytes, archiveBytes = MaxArchiveBytes, totalArchiveBytes = 4 * MaxArchiveBytes,
                patchOperations = MaxPatchOperations, activeOperations = 1, activeOutcomeObservations = 1, queuedOperations = 0 },
            next = "Use sed_discover topic=query/select/snapshot or an operation name for its exact domain schema. Domain operations are shared with sed-agent." },
        "query" => AgentQueries.Schema(), "select" => AgentSelections.Schema(), "patch" => AgentSchema.Patch(),
        "snapshot" => AgentSnapshotRequest.Schema(), "resources" => AgentWorkspace.ResourceRequestSchema(),
        "bindings" => AgentWorkspace.BindingsRequestSchema(), "lighting" => AgentWorkspace.LightingRequestSchema(),
        "vertexColors" => AgentWorkspace.VertexColorsRequestSchema(),
        "assets" => AgentWorkspace.AssetsRequestSchema(), _ => AgentSchema.Patch(topic) };

    private static void ValidateArguments(string name, IDictionary<string, JsonElement> args)
    {
        var tool = ToolCatalog.Tools.SingleOrDefault(t => t.Name == name) ?? throw new AgentRequestException("Unknown SED tool.");
        var properties = tool.InputSchema.GetProperty("properties");
        foreach (var key in args.Keys) if (!properties.TryGetProperty(key, out _)) throw new AgentRequestException($"Unknown {name} argument: {key}.");
        foreach (var key in tool.InputSchema.GetProperty("required").EnumerateArray())
            if (!args.ContainsKey(key.GetString()!)) throw new AgentRequestException($"Missing {key.GetString()}.");
        var encoded = JsonSerializer.SerializeToUtf8Bytes(args, Json);
        if (encoded.Length > AgentWorkspace.MaxRequestBytes + 64 * 1024) throw new AgentRequestException("Tool arguments exceed the byte budget.");
        using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions { MaxDepth = 20 });
        CheckUnique(document.RootElement);
    }
    private static void CheckUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new AgentRequestException("Duplicate JSON properties are not supported."); CheckUnique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckUnique(item);
    }
    private static string RequestHash(string command, IDictionary<string, JsonElement> args)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("command", command); writer.WritePropertyName("arguments"); writer.WriteStartObject();
            foreach (var item in args.OrderBy(p => p.Key, StringComparer.Ordinal))
            { writer.WritePropertyName(item.Key); Canonical(writer, item.Value); }
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        return AgentWorkspace.Hash(stream.ToArray());
    }
    private static void Canonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        { writer.WriteStartObject(); foreach (var p in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(p.Name); Canonical(writer, p.Value); } writer.WriteEndObject(); }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Canonical(writer, item); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }

    private static JsonElement BoundedElement(object value, int maxBytes = MaxResultBytes)
    {
        using var stream = new LimitedResultStream(maxBytes);
        JsonSerializer.Serialize(stream, value, Json);
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
    private static CallToolResult Success(object result, byte[]? image = null)
    {
        var envelope = BoundedElement(new { schemaVersion = 1, ok = true, result });
        List<ContentBlock> content = [new TextContentBlock { Text = envelope.GetRawText() }];
        if (image is not null) content.Add(ImageContentBlock.FromBytes(image, "image/png"));
        return new() { IsError = false, StructuredContent = envelope, Content = content };
    }
    internal static CallToolResult SnapshotSuccess(SnapshotWorkerResult result)
    {
        string standardError = Short(result.StandardError);
        bool standardErrorTruncated = standardError.Length != result.StandardError.Length;
        try
        {
            return Success(new { result.Receipt, result.Sha256, result.OutputState, standardError, standardErrorTruncated }, result.ImageBytes);
        }
        catch (AgentRequestException)
        {
            // The worker has already produced and verified the PNG. Preserve that
            // evidence even if a valid but verbose receipt cannot fit the envelope.
            return Failure("snapshot_result_too_large", "Preview was created, but its full receipt exceeds the response budget. Inspect outputState; do not retry into the same path.",
                new { result.Sha256, result.OutputState, receiptOmitted = true, standardError, standardErrorTruncated });
        }
    }
    internal static CallToolResult SnapshotFailure(SnapshotWorkerException error)
    {
        string standardError = Short(error.StandardError);
        bool standardErrorTruncated = standardError.Length != error.StandardError.Length;
        try
        {
            var details = BoundedElement(new { error.IsHostFault, error.ExitCode, error.Receipt, error.OutputState, standardError, standardErrorTruncated }, MaxResultBytes - 128 * 1024);
            return Failure(error.Code, error.Message, details);
        }
        catch (AgentRequestException)
        {
            return Failure(error.Code, error.Message, new { error.IsHostFault, error.ExitCode, error.OutputState,
                receiptOmitted = true, standardError, standardErrorTruncated });
        }
    }
    private static CallToolResult Failure(string code, string message, object? details = null)
    {
        JsonElement envelope;
        try { envelope = BoundedElement(new { schemaVersion = 1, ok = false, error = new { code, message = Short(message), details } }); }
        catch (AgentRequestException) { envelope = BoundedElement(new { schemaVersion = 1, ok = false, error = new { code, message = Short(message), detailsOmitted = "Diagnostics exceed response budget; use focused queries." } }); }
        return new() { IsError = true, StructuredContent = envelope, Content = [new TextContentBlock { Text = envelope.GetRawText() }] };
    }
    public void Dispose() { admission.Dispose(); observationAdmission.Dispose(); }
    private sealed class LimitedResultStream(int max) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Limit(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Limit(buffer.Length); base.Write(buffer); }
        private void Limit(int count) { if (Position + count > max) throw new AgentRequestException("Response exceeds 2 MiB; narrow the request."); }
    }
}

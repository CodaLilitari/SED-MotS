using System.Text;
using System.Text.Json;

namespace Sed.Formats.NativeCore;

internal static class CppCoreProtocol
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] Common = ["schemaVersion", "name", "version", "upstreamCommit", "sourceManifestSha256",
        "operation", "requestId", "status", "readOnly", "inputBytes"];

    internal static JsonElement Validate(byte[] bytes, int exitCode, string operation, string? requestId,
        string? kind, int inputBytes, string manifestHash)
    {
        try
        {
            Need(bytes.Length is > 0 and <= CppCoreInspector.MaxOutputBytes);
            Need(bytes[^1] == (byte)'\n');
            // Validate encoding independently: no replacement characters may
            // conceal invalid bytes in the worker's protocol channel.
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            var root = document.RootElement;
            CheckUniqueKeys(root);
            Need(root.ValueKind == JsonValueKind.Object);
            Integer(root, "schemaVersion", 1);
            Text(root, "name", "sed-cpp-bridge"); Text(root, "version", "0.1.0");
            Text(root, "upstreamCommit", CppCoreInspector.UpstreamCommit);
            Text(root, "sourceManifestSha256", manifestHash);
            Text(root, "operation", operation); Boolean(root, "readOnly", true);
            Integer(root, "inputBytes", inputBytes);
            if (requestId is null) Need(root.GetProperty("requestId").ValueKind == JsonValueKind.Null);
            else Text(root, "requestId", requestId);
            string status = String(root, "status");
            if (status != "ok")
            {
                Keys(root, Common.Concat(["error"]));
                var error = root.GetProperty("error");
                Keys(error, ["code", "message"]);
                string code = String(error, "code");
                Need(String(error, "message").Length <= 2048);
                Need(operation != "capabilities");
                Need((exitCode, status, code) is
                    (2, "rejected", "invalid_request" or "input_limit") or
                    (3, "rejected", "parse_error" or "project_kind_mismatch") or
                    (5, "failed", "io_error") or
                    (6, "failed", "internal_error" or "output_limit"));
                return root.Clone();
            }
            Need(exitCode == 0);
            if (operation == "capabilities") ValidateCapabilities(root);
            else ValidateInspection(root, operation, kind!);
            return root.Clone();
        }
        catch (CppCoreFault) { throw; }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or EncoderFallbackException
            or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new CppCoreFault("protocol_error", "The native worker response is not a valid bound protocol-1 observation.", true, error); }
    }

    private static void ValidateCapabilities(JsonElement root)
    {
        Keys(root, Common.Concat(["capabilities", "projectKinds", "writesFiles", "limits"]));
        StringsExactly(root.GetProperty("capabilities"), ["inspect-native-sed", "inspect-jkl"]);
        StringsExactly(root.GetProperty("projectKinds"), ["jk", "mots", "ijim"]);
        Boolean(root, "writesFiles", false);
        var limits = root.GetProperty("limits");
        Keys(limits, ["maxInputBytes", "maxOutputBytes", "maxDiagnostics", "maxDiagnosticBytes", "maxOptionalSections", "maxIntegrityItems",
            "maxJklLines", "maxJklLineBytes", "maxJklTokens", "maxJklSurfaceReferences", "maxJklSurfaceExpansionTokens", "maxJklExpansionBytes"]);
        Integer(limits, "maxInputBytes", CppCoreInspector.MaxInputBytes);
        Integer(limits, "maxOutputBytes", CppCoreInspector.MaxOutputBytes);
        Integer(limits, "maxDiagnostics", 32); Integer(limits, "maxDiagnosticBytes", 256);
        Integer(limits, "maxOptionalSections", 32); Integer(limits, "maxIntegrityItems", 32);
        Integer(limits, "maxJklLines", 131072); Integer(limits, "maxJklLineBytes", 65536);
        Integer(limits, "maxJklTokens", 1048576); Integer(limits, "maxJklSurfaceReferences", 131072);
        Integer(limits, "maxJklSurfaceExpansionTokens", 1048576); Integer(limits, "maxJklExpansionBytes", 67108864);
    }

    private static void ValidateInspection(JsonElement root, string operation, string kind)
    {
        Keys(root, Common.Concat(["projectKind", "uvCoordinateSpace", "summary", "resourceValidation",
            "diagnostics", "diagnosticsTruncated", "integrity", "optionalSections", "optionalSectionsTruncated"]));
        Text(root, "projectKind", kind);
        Need(String(root, "uvCoordinateSpace") is "texture-pixels" or "normalized");
        var summary = root.GetProperty("summary");
        string[] counts = ["sectors", "surfaces", "vertices", "things", "templates", "cogs", "lights", "materials", "colormaps", "architecturalLightingObjects"];
        Keys(summary, counts);
        foreach (string count in counts) Nonnegative(summary, count);
        var resources = root.GetProperty("resourceValidation");
        Keys(resources, ["status", "complete", "cogHydration", "uvNormalization"]);
        Text(resources, "status", "not-performed"); Boolean(resources, "complete", false);
        Text(resources, "cogHydration", "deferred"); Text(resources, "uvNormalization", "not-performed");
        var diagnostics = Array(root, "diagnostics", 32);
        foreach (var diagnostic in diagnostics.EnumerateArray())
        {
            Keys(diagnostic, ["severity", "message"]);
            Need(String(diagnostic, "severity") is "verbose" or "trace" or "debug" or "info" or "warning" or "error" or "fatal");
            // Invalid source bytes are escaped as U+00xx by the native worker:
            // 256 source bytes can therefore decode to 512 UTF-8 bytes.
            string message = String(diagnostic, "message");
            Need(message.Length <= 256 && StrictUtf8.GetByteCount(message) <= 512);
        }
        _ = root.GetProperty("diagnosticsTruncated").GetBoolean();
        var integrity = root.GetProperty("integrity");
        Keys(integrity, ["status", "hadCorruption", "repairedChunks", "repairedSections", "unreadableRanges", "truncated"]);
        string integrityStatus = String(integrity, "status");
        Need(integrityStatus is "not-applicable" or "clean" or "repaired");
        bool corrupt = integrity.GetProperty("hadCorruption").GetBoolean();
        long repairs = Nonnegative(integrity, "repairedChunks");
        var sections = Array(integrity, "repairedSections", 32);
        foreach (var section in sections.EnumerateArray()) Need(section.ValueKind == JsonValueKind.String && section.GetString()!.Length <= 256);
        var ranges = Array(integrity, "unreadableRanges", 32);
        foreach (var range in ranges.EnumerateArray())
        {
            Keys(range, ["offset", "length"]); Nonnegative(range, "offset"); Nonnegative(range, "length");
        }
        bool truncated = integrity.GetProperty("truncated").GetBoolean();
        var optional = Array(root, "optionalSections", 32);
        foreach (var section in optional.EnumerateArray())
        {
            Keys(section, ["type", "payloadBytes"]);
            Need(String(section, "type").Length == 4); Nonnegative(section, "payloadBytes");
        }
        bool optionalTruncated = root.GetProperty("optionalSectionsTruncated").GetBoolean();
        if (operation == "inspect-jkl")
            Need(integrityStatus == "not-applicable" && !corrupt && repairs == 0 && sections.GetArrayLength() == 0
                && ranges.GetArrayLength() == 0 && !truncated && optional.GetArrayLength() == 0 && !optionalTruncated);
        else
        {
            Need(integrityStatus is "clean" or "repaired");
            if (integrityStatus == "clean")
                Need(!corrupt && repairs == 0 && sections.GetArrayLength() == 0 && ranges.GetArrayLength() == 0 && !truncated);
            else Need(corrupt);
        }
    }

    private static void CheckUniqueKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Need(names.Add(property.Name));
                CheckUniqueKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var element in value.EnumerateArray()) CheckUniqueKeys(element);
    }

    private static void Keys(JsonElement value, IEnumerable<string> names)
    {
        Need(value.ValueKind == JsonValueKind.Object);
        var expected = names.ToHashSet(StringComparer.Ordinal);
        Need(value.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(expected));
    }
    private static JsonElement Array(JsonElement value, string name, int limit)
    {
        var array = value.GetProperty(name);
        Need(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() <= limit);
        return array;
    }
    private static void StringsExactly(JsonElement value, string[] expected)
    {
        Need(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == expected.Length);
        Need(value.EnumerateArray().Select(v => v.GetString()).SequenceEqual(expected));
    }
    private static string String(JsonElement value, string name)
    {
        string? text = value.GetProperty(name).GetString();
        Need(text is not null); _ = StrictUtf8.GetByteCount(text!); return text!;
    }
    private static void Text(JsonElement value, string name, string expected) => Need(String(value, name) == expected);
    private static void Boolean(JsonElement value, string name, bool expected) => Need(value.GetProperty(name).GetBoolean() == expected);
    private static void Integer(JsonElement value, string name, long expected) => Need(Nonnegative(value, name) == expected);
    private static long Nonnegative(JsonElement value, string name)
    {
        Need(value.GetProperty(name).TryGetInt64(out long number) && number >= 0); return number;
    }
    private static void Need(bool condition)
    {
        if (!condition) throw new CppCoreFault("protocol_error", "The native worker response does not match its pinned read-only protocol-1 contract.");
    }
}

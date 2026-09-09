using System.Text;
using System.Text.Json;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Formats.Automation;

/// <summary>CPU-only contract for one provenance-bound diagnostic image.</summary>
public sealed record AgentSnapshotRequest(string InputSha256, string? SourceRevision,
    Vec3 Position, Vec3 Target, double FieldOfViewDegrees, int Width, int Height,
    double NearPlane, double FarPlane)
{
    public const int MinDimension = 64;
    public const int MaxDimension = 2048;
    public const int MaxPixels = 2_097_152;
    public const int MaxTriangles = 250_000;

    public static object Schema()
    {
        object Hash() => new { type = "string", pattern = "^[0-9a-fA-F]{64}$" };
        object Vector() => new { type = "array", minItems = 3, maxItems = 3,
            items = new { type = "number", minimum = -1_000_000, maximum = 1_000_000 } };
        return new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "SED agent diagnostic snapshot version 1",
            ["description"] = "One flat diagnostic geometry PNG using offscreen Vulkan, without a window. Camera coordinates are engine world units with Z up. Distinct position/target must not look vertically along Z. Requires width*height <= 2097152 and nearPlane < farPlane. Native inputs require sourceRevision. No textures, game lighting, 3DO things, depth image, or object-ID image are rendered.",
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new[] { "schemaVersion", "inputSha256", "position", "target", "fieldOfViewDegrees", "width", "height" },
            ["properties"] = new Dictionary<string, object>
            {
                ["schemaVersion"] = new Dictionary<string, object> { ["const"] = 1 },
                ["inputSha256"] = Hash(), ["sourceRevision"] = Hash(),
                ["position"] = Vector(), ["target"] = Vector(),
                ["fieldOfViewDegrees"] = new { type = "number", minimum = 10, maximum = 140 },
                ["width"] = new { type = "integer", minimum = MinDimension, maximum = MaxDimension },
                ["height"] = new { type = "integer", minimum = MinDimension, maximum = MaxDimension },
                ["nearPlane"] = new { type = "number", minimum = 0.001, maximum = 1000, @default = 0.01 },
                ["farPlane"] = new { type = "number", minimum = 0.01, maximum = 100000, @default = 1000 },
            },
        };
    }

    public static AgentSnapshotRequest Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > AgentWorkspace.MaxRequestBytes)
            throw new AgentRequestException("Snapshot request exceeds 1 MiB.");
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new AgentRequestException("Expected a snapshot request object.");
        string[] required = ["schemaVersion", "inputSha256", "position", "target", "fieldOfViewDegrees", "width", "height"];
        string[] optional = ["sourceRevision", "nearPlane", "farPlane"];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!seen.Add(property.Name) || (!required.Contains(property.Name) && !optional.Contains(property.Name)))
                throw new AgentRequestException($"Duplicate or unsupported snapshot property '{property.Name}'.", jsonPointer: "/" + property.Name);
        foreach (string name in required)
            if (!seen.Contains(name)) throw new AgentRequestException($"Missing snapshot property '{name}'.", jsonPointer: "/" + name);
        int Integer(string key, int min, int max)
        {
            var value = root.GetProperty(key);
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int n) || n < min || n > max)
                throw new AgentRequestException($"{key} must be an integer from {min} through {max}.", jsonPointer: "/" + key);
            return n;
        }
        double Number(string key, double min, double max, double? fallback = null)
        {
            if (!root.TryGetProperty(key, out var value) && fallback is double f) return f;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n) || n < min || n > max)
                throw new AgentRequestException($"{key} must be finite and from {min} through {max}.", jsonPointer: "/" + key);
            return n;
        }
        Vec3 Vector(string key)
        {
            var value = root.GetProperty(key);
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
                throw new AgentRequestException($"{key} must contain three coordinates.", jsonPointer: "/" + key);
            var coordinates = new double[3];
            int i = 0;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out double n) || !double.IsFinite(n) || System.Math.Abs(n) > 1_000_000)
                    throw new AgentRequestException($"{key} contains an invalid coordinate.", jsonPointer: $"/{key}/{i}");
                coordinates[i++] = n;
            }
            return new Vec3(coordinates[0], coordinates[1], coordinates[2]);
        }
        string Hash(string key)
        {
            var value = root.GetProperty(key);
            string? hash = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (hash is null || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c)))
                throw new AgentRequestException($"{key} must be a 64-digit SHA256 from inspect/query.", jsonPointer: "/" + key);
            return hash.ToLowerInvariant();
        }
        _ = Integer("schemaVersion", 1, 1);
        int width = Integer("width", MinDimension, MaxDimension), height = Integer("height", MinDimension, MaxDimension);
        if ((long)width * height > MaxPixels) throw new AgentRequestException($"Snapshot exceeds the {MaxPixels}-pixel budget.", jsonPointer: "/width");
        var position = Vector("position"); var target = Vector("target");
        var delta = target - position;
        if (delta.Length < 0.000001 || delta.Cross(new Vec3(0, 0, 1)).Length / delta.Length < 0.000001)
            throw new AgentRequestException("Camera position and target must differ and cannot look directly along the Z-up axis.", jsonPointer: "/target");
        double near = Number("nearPlane", 0.001, 1000, 0.01), far = Number("farPlane", 0.01, 100000, 1000);
        if (near >= far) throw new AgentRequestException("nearPlane must be less than farPlane.", jsonPointer: "/nearPlane");
        return new(Hash("inputSha256"), seen.Contains("sourceRevision") ? Hash("sourceRevision") : null,
            position, target, Number("fieldOfViewDegrees", 10, 140), width, height, near, far);
    }

    public void VerifySource(AgentDocumentSnapshot snapshot)
    {
        if (!string.Equals(InputSha256, snapshot.InputSha256, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException("Snapshot input changed since inspection. Inspect again before rendering.");
        if (snapshot.Dependencies.Count != 0 && SourceRevision is null)
            throw new AgentRequestException("Native snapshots require sourceRevision from inspect/query.", jsonPointer: "/sourceRevision");
        if (SourceRevision is not null && !string.Equals(SourceRevision, snapshot.SourceRevision, StringComparison.OrdinalIgnoreCase))
            throw new AgentConflictException("Snapshot source or native template dependency changed since inspection.");
    }

    /// <summary>Fresh destination preflight; callers must still use an atomic no-clobber final move.</summary>
    public static string PrepareOutputPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
            throw new AgentRequestException("Snapshot output must have the .png extension.");
        if (File.Exists(path) || Directory.Exists(path)) throw new AgentConflictException("Snapshot output already exists; choose a new file.");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new AgentRequestException("Snapshot output directory must exist.");
        return path;
    }

    /// <summary>Diagnostic policy: reciprocal visible portals without a drawable face are openings.</summary>
    public static bool IsOpenPortal(Surface surface) => surface.Adjoin is { } other
        && ReferenceEquals(other.Adjoin, surface)
        && (surface.AdjoinFlags & AdjoinFlags.Visible) != 0
        && (surface.Geo == 0 || string.IsNullOrWhiteSpace(surface.Material));
}

using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Jkl;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    public const int MaxBatchUvTargets = 256;
    public const int MaxBatchUvCornersPerPatch = 4096;
    public const int MaxArrayOutputsPerPatch = 1024;

    private static List<(int sector, int surface, Surface value)> BatchUvTargets(JklDocument doc, JsonElement op)
    {
        if (!op.TryGetProperty("surfaces", out var array) || array.ValueKind != JsonValueKind.Array
            || array.GetArrayLength() is < 1 or > MaxBatchUvTargets)
            throw FieldError($"surfaces must contain 1..{MaxBatchUvTargets} explicit sector/surface addresses.", "surfaces");
        var targets = new List<(int sector, int surface, Surface value)>();
        var seen = new HashSet<Surface>();
        foreach (var address in array.EnumerateArray())
        {
            try
            {
                Fields(address, "sector", "surface");
                int sector = Integer(address, "sector"), surface = Integer(address, "surface");
                var value = At(At(doc.Level.Sectors, sector, "sector").Surfaces, surface, "surface");
                if (!seen.Add(value)) throw new AgentRequestException("surfaces must not contain duplicate targets.");
                targets.Add((sector, surface, value));
            }
            catch (AgentRequestException error)
            {
                throw new AgentRequestException(error.Message, jsonPointer: $"/surfaces/{targets.Count}" + error.JsonPointer, innerException: error);
            }
        }
        return targets;
    }

    private static TexVertex UvPair(JsonElement op, string key)
    {
        if (!op.TryGetProperty(key, out var pair) || pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2)
            throw FieldError($"{key} must contain exactly two texel coordinates.", key);
        var values = pair.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var n)
            && double.IsFinite(n) && System.Math.Abs(n) <= 1_000_000 ? n
            : throw FieldError($"{key} coordinates must be finite and within +/-1000000.", key)).ToArray();
        return new TexVertex(values[0], values[1]);
    }

    private static bool TryPrepareBatchUvOperation(JklDocument doc, JsonElement op, out IEditCommand? command)
    {
        command = null;
        string name = String(op, "op");
        if (name is not ("projectSurfaceUv" or "transformSurfaceUv")) return false;
        Fields(op, ["op", .. OperationFields[name]]);
        var surfaces = BatchUvTargets(doc, op).Select(t => t.value).ToArray();
        if (name == "projectSurfaceUv")
        {
            var direction = String(op, "direction") switch
            {
                "+x" => SurfaceUvProjectionDirection.PositiveX, "-x" => SurfaceUvProjectionDirection.NegativeX,
                "+y" => SurfaceUvProjectionDirection.PositiveY, "-y" => SurfaceUvProjectionDirection.NegativeY,
                "+z" => SurfaceUvProjectionDirection.PositiveZ, "-z" => SurfaceUvProjectionDirection.NegativeZ,
                _ => throw FieldError("direction must be +x, -x, +y, -y, +z or -z.", "direction"),
            };
            double density = Number(op, "texelsPerUnit", 0, 1_000_000);
            if (density == 0) throw FieldError("texelsPerUnit must be positive.", "texelsPerUnit");
            command = BatchSurfaceUvCommand.Project(surfaces, direction, Vector(op, "origin"), density);
        }
        else
            command = BatchSurfaceUvCommand.Transform(surfaces, UvPair(op, "pivot"), UvPair(op, "scale"),
                Number(op, "rotationDegrees", -1_000_000, 1_000_000), UvPair(op, "offset"));
        return true;
    }

    private static object BatchUvEffect(JklDocument doc, JsonElement op) => new
    {
        target = new { kind = "surfaces" },
        state = BatchUvTargets(doc, op).Select(t => new
        {
            sector = t.sector, surface = t.surface,
            uv = t.value.Corners.Select(c => new[] { c.Uv.U, c.Uv.V }).ToArray(),
        }).ToArray(),
    };
}

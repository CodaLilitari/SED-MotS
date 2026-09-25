using System.Text.Json;
using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Model;
using Sed.Formats.Jkl;
using Sed.Formats.Resources;
using Sed.Formats.ThreeDo;

namespace Sed.Formats.Automation;

public static partial class AgentWorkspace
{
    private static bool TryPrepareArchitectureOperation(JklDocument doc, JsonElement op,
        ExplicitArchiveResources? resources, out IEditCommand? command)
    {
        command = null;
        string name = String(op, "op");
        if (name is not ("bindThingArchitecture" or "rebindThingArchitecture" or "bakeThingArchitecture")) return false;
        Fields(op, ["op", .. OperationFields[name]]);
        var thing = At(doc.Level.Things, Integer(op, "thing"), "thing");
        if (resources is null)
            throw new AgentRequestException("Architectural authoring requires explicit hash-bound resourceArchives. Use schema resources and review the resulting resourceRevision.");
        if (name == "rebindThingArchitecture" && String(op, "policy") != "reset")
            throw FieldError("Rebind requires policy 'reset': it replaces this thing's samples with a private zeroed allocation.", "policy");
        if (name == "bakeThingArchitecture" && !ArchitecturalLightingAuthoring.IsBakeProfile(String(op, "profile")))
            throw FieldError("Choose source-points-v1 (unoccluded) or source-points-world-occluded-v1 (world polygon blockers). Neither adds ambient, model/self shadows, alpha-aware tracing or retail parity.", "profile");
        string action = name switch { "bindThingArchitecture" => "bind", "rebindThingArchitecture" => "rebind", _ => "bake" };
        command = ArchitectureAuthoringService.Prepare(doc.Level, thing, resources, action,
            name == "bindThingArchitecture" ? Integer(op, "lightingIndex") : null,
            name == "bakeThingArchitecture" ? String(op, "profile") : ArchitecturalLightingAuthoring.BakeProfile);
        return true;
    }

    internal static object DescribeArchitecture(Level level, Thing thing)
    {
        var samples = thing.ArchLightingIndex >= 0 && thing.ArchLightingIndex < level.ArchitecturalLighting.Count
            ? level.ArchitecturalLighting[thing.ArchLightingIndex] : null;
        return new
        {
            status = ArchitecturalLightingAuthoring.Status(level, thing),
            resourceStatus = "unverified-use-resource-validation",
            provenance = thing.ArchLightingProvenance,
            samplesSha256 = samples is null ? null : ArchitecturalLightingAuthoring.SamplesDigest(samples),
            meshVertexCounts = samples?.Meshes.Select(m => m.Vertices.Count).ToArray(),
            profileLimits = thing.ArchLightingProvenance?.Origin switch
            {
                ArchitecturalLightingAuthoring.BakeProfile => "Unoccluded, non-directional source-point samples; no ambient, self/model shadows, animation, or retail-equivalence claim.",
                ArchitecturalLightingAuthoring.WorldOccludedBakeProfile => "Non-directional source points with world polygon blockers and BlockLight portal flags; NoBlock bypasses rays. No ambient, model/self shadows, material-alpha tracing, animation or retail equivalence. Render/fullbright modes do not make walls transmit light.",
                _ => (string?)null,
            },
        };
    }

    private static object ArchitectureOperationEffect(JklDocument doc, JsonElement op)
    {
        int index = Integer(op, "thing");
        var thing = At(doc.Level.Things, index, "thing");
        return new { target = new { kind = "thing", index }, state = new {
            thing.ArchLightingIndex, thing.ArchLightingSourceModel, thing.ArchLightingSourceModelHash,
            architecture = DescribeArchitecture(doc.Level, thing) } };
    }
}

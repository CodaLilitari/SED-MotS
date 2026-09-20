using System.Text;
using Sed.Core.Editing;
using Sed.Core.Lighting;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.ThreeDo;

namespace Sed.Formats.Resources;

/// <summary>Prepares explicit ARCH edits from the same captured bytes used by resource validation.</summary>
public static class ArchitectureAuthoringService
{
    public static ReplaceThingArchitectureCommand Prepare(Level level, Thing thing,
        ExplicitArchiveResources resources, string action, int? lightingIndex = null)
    {
        ArgumentNullException.ThrowIfNull(level); ArgumentNullException.ThrowIfNull(thing);
        ArgumentNullException.ThrowIfNull(resources);
        if (level.Kind != ProjectType.MysteriesOfTheSith || !level.Things.Contains(thing) || thing.Level != level)
            throw new ArgumentException("Architectural authoring requires a live MotS thing.");
        if (!ArchitectureBindingValidator.IsLevelGeometry(level, thing))
            throw new ArgumentException("Architectural authoring requires resolved thingflags level-geometry bit 0x8.");
        if (action is not ("bind" or "rebind" or "bake")) throw new ArgumentException("Architecture action must be bind, rebind or bake.");
        if (action == "bind" && (thing.ArchLightingIndex != -1 || lightingIndex is null))
            throw new ArgumentException("Bind requires an unbound thing and an explicit existing lightingIndex.");
        if (action != "bind" && lightingIndex.HasValue)
            throw new ArgumentException("lightingIndex is only supported by bind.");
        string name = level.GetThingModel(thing);
        if (string.IsNullOrWhiteSpace(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The thing has no resolved model3d.");
        var resolved = resources.ResolveArchitectureModel(name)
            ?? throw new InvalidOperationException($"Model '{name}' is missing, ambiguous or unsupported in the captured resources.");
        var captured = resources.Read(name, "3do")
            ?? throw new InvalidOperationException("The captured model is unavailable.");
        // Parse only this captured entry. A preview cache may refer to different
        // bytes even when it has the same model name.
        byte[] bytes = captured.ToArray();
        if (AgentWorkspace.Hash(bytes) != resolved.RawSha256)
            throw new InvalidOperationException("Captured model bytes no longer match their resource receipt.");
        ArchitecturalLightObject samples;
        string origin, inputHash = string.Empty;
        if (action == "bind")
        {
            var validation = ArchitectureBindingValidator.ValidateBindingCandidate(level, thing, resolved, lightingIndex!.Value);
            if (!validation.CanUseBinding)
                throw new InvalidOperationException("Cannot bind this allocation: " + string.Join(" ", validation.Diagnostics.Select(d => d.Message)));
            samples = level.ArchitecturalLighting[lightingIndex.Value].Clone();
            origin = "bound";
        }
        else
        {
            if (action == "bake" && thing.ArchLightingProvenance is { } previous &&
                (!previous.ModelName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                 !previous.ModelSha256.Equals(resolved.RawSha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Accepted model bytes changed. Rebind with reset before baking the replacement model.");
            var points = ArchitectureAuthoringGeometry.Prepare(Encoding.Latin1.GetString(bytes), resolved.Topology!, thing);
            if (action == "bake")
            {
                samples = ArchitecturalLightingAuthoring.PrepareSamples(level, points);
                origin = ArchitecturalLightingAuthoring.BakeProfile;
                inputHash = ArchitecturalLightingAuthoring.InputsDigest(level, thing);
            }
            else
            {
                samples = new();
                foreach (var vertices in points)
                {
                    var mesh = new ArchitecturalLightMesh();
                    mesh.Vertices.AddRange(new ArchitecturalLightVertex[vertices.Count]);
                    samples.Meshes.Add(mesh);
                }
                origin = "reset";
            }
        }
        var provenance = new ArchitectureLightingProvenance(origin, name, resolved.RawSha256, inputHash,
            ArchitecturalLightingAuthoring.SamplesDigest(samples));
        resources.VerifyUnchanged();
        return new ReplaceThingArchitectureCommand(level, thing, samples, name, provenance);
    }
}

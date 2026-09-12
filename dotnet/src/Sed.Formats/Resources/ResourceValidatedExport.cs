using System.Text.Json;
using Sed.Core.Model;
using Sed.Formats.Automation;
using Sed.Formats.Jkl;
using Sed.Formats.ThreeDo;

namespace Sed.Formats.Resources;

public sealed record ArchitectureResourceValidationResult(string Status, int BoundThings,
    string? ResourceRevision, IReadOnlyList<ArchitectureBindingValidation> Bindings,
    IReadOnlyList<ResourceArchiveReceipt> Archives, IReadOnlyList<ResourceEntryReceipt> UsedResources,
    IReadOnlyList<string> Diagnostics)
{
    public bool HasErrors => Bindings.Any(b => !b.CanUseBinding);
}

/// <summary>Validates the live document before JKL serialization loses native fingerprint provenance.</summary>
public static class ArchitectureResourceValidation
{
    public static ArchitectureResourceValidationResult Evaluate(Level level, IArchitectureResourceProvider? resources)
    {
        var things = level.Things.Where(t => t.ArchLightingIndex >= 0).ToArray();
        var bindings = new List<ArchitectureBindingValidation>();
        if (resources is not null)
            foreach (var thing in things)
            {
                string name = level.GetThingModel(thing);
                var model = string.IsNullOrWhiteSpace(name) ? null : resources.ResolveArchitectureModel(name);
                bindings.Add(ArchitectureBindingValidator.Validate(level, thing, model));
            }
        string status = things.Length == 0 ? "not-applicable"
            : resources is null ? "unverified-no-resources"
            : bindings.Any(b => !b.CanUseBinding) ? "invalid" : "compatible-current-resources";
        return new(status, things.Length, resources?.ResourceRevision, bindings.AsReadOnly(),
            Array.AsReadOnly(resources?.Archives.ToArray() ?? []), Array.AsReadOnly(resources?.UsedResources.ToArray() ?? []),
            Array.AsReadOnly(resources?.Diagnostics.ToArray() ?? []));
    }
}

public sealed record PreparedResourceExport(string OutputText, ArchitectureResourceValidationResult Validation);

public static class ResourceValidatedExport
{
    public static PreparedResourceExport Prepare(JklDocument document, IArchitectureResourceProvider? resources)
    {
        var validation = ArchitectureResourceValidation.Evaluate(document.Level, resources);
        if (validation.HasErrors)
            throw new AgentValidationException("Architectural lighting is incompatible with the captured model resources. No export was written.", validation);
        string text = JklWriter.Build(document);
        resources?.VerifyUnchanged();
        return new(text, validation);
    }

    /// <summary>Includes native authoring fingerprints, which do not survive a JKL reparse.</summary>
    public static string DocumentRevision(JklDocument document) => AgentWorkspace.Hash(JsonSerializer.SerializeToUtf8Bytes(new {
        version = "sed-resource-export-document-v1", output = JklWriter.Build(document),
        architecture = document.Level.Things.Select(t => new {
            t.ArchLightingIndex, t.ArchLightingSourceModel, t.ArchLightingSourceModelHash }).ToArray() }));
}

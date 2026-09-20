using System.Globalization;
using System.Text.Json.Serialization;
using Sed.Core.Model;

namespace Sed.Formats.ThreeDo;

/// <summary>Captured model input. RawSha256 is an entry hash, not the normalized historical baseline.</summary>
public sealed record ResolvedArchitectureModel(string ModelName, string ResourceIdentity, string RawSha256, ThreeDoTopology? Topology);

[JsonConverter(typeof(JsonStringEnumConverter<ArchitectureDiagnosticSeverity>))]
public enum ArchitectureDiagnosticSeverity { Warning, Error }
[JsonConverter(typeof(JsonStringEnumConverter<ArchitectureBaselineStatus>))]
public enum ArchitectureBaselineStatus { NotBound, Missing, Unsupported, Matches, Mismatch }
[JsonConverter(typeof(JsonStringEnumConverter<ArchitectureDiagnosticCode>))]
public enum ArchitectureDiagnosticCode
{
    UnsupportedProject, MissingArchitectureObject, MissingModel, ResolvedModelNameMismatch,
    SourceModelNameMismatch, IneligibleThingFlags, InvalidThingFlags, MissingHistoricalBaseline,
    UnsupportedFingerprintNormalization, InvalidHistoricalFingerprint, ModelContentMismatch,
    MissingTopology, InvalidTopology, TopLevelMeshMismatch, TopLevelVertexMismatch,
    LowerLodMeshOverflow, LowerLodVertexOverflow, AuthoredModelBytesMismatch,
}

public sealed record ArchitectureBindingDiagnostic(ArchitectureDiagnosticCode Code,
    ArchitectureDiagnosticSeverity Severity, string Message, int? Geoset = null, int? Mesh = null);

/// <summary>Current shape compatibility and historical evidence are deliberately separate.</summary>
public sealed record ArchitectureBindingValidation(int ThingIndex, int ArchitectureIndex, string ModelName,
    string? ResourceIdentity, string? RawSha256, bool TopologyCompatible,
    ArchitectureBaselineStatus BaselineStatus, IReadOnlyList<ArchitectureBindingDiagnostic> Diagnostics)
{
    public bool CanUseBinding => ArchitectureIndex >= 0 && TopologyCompatible &&
        !Diagnostics.Any(diagnostic => diagnostic.Severity == ArchitectureDiagnosticSeverity.Error);
    public bool HistoricalBaselineVerified => CanUseBinding && BaselineStatus == ArchitectureBaselineStatus.Matches;
}

/// <summary>
/// Resource-independent ARCH binding checks shared by preview and transactions.
/// No arrays, baselines, resources or thing parameters are changed. A compatible
/// current model without a historical fingerprint never becomes an old-bake certification.
/// </summary>
public static class ArchitectureBindingValidator
{
    public const string Version = "2";

    /// <summary>Instance thingflags replace the closest inherited value; they do not merge with it.</summary>
    public static bool IsLevelGeometry(Level level, Thing thing)
    {
        string value = thing.Values.TryGetValue("thingflags", out string? direct) ? direct : level.GetTemplateValue(thing.Template, "thingflags");
        if (string.IsNullOrEmpty(value)) return false;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint flags))
            throw new FormatException("Thing has invalid hexadecimal thingflags.");
        return (flags & 0x8) != 0;
    }

    public static ArchitectureBindingValidation Validate(Level level, Thing thing, ResolvedArchitectureModel? resolvedModel)
        => ValidateCore(level, thing, resolvedModel, thing.ArchLightingIndex, checkStoredBaseline: true);

    /// <summary>Checks an explicit allocation for a new binding without mutating the thing or asserting bake history.</summary>
    internal static ArchitectureBindingValidation ValidateBindingCandidate(Level level, Thing thing,
        ResolvedArchitectureModel resolvedModel, int architectureIndex)
        => ValidateCore(level, thing, resolvedModel, architectureIndex, checkStoredBaseline: false);

    private static ArchitectureBindingValidation ValidateCore(Level level, Thing thing,
        ResolvedArchitectureModel? resolvedModel, int architectureIndex, bool checkStoredBaseline)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(thing);
        int index = level.Things.IndexOf(thing);
        string modelName = level.GetThingModel(thing);
        var diagnostics = new List<ArchitectureBindingDiagnostic>();
        bool compatible = false;
        ArchitectureBaselineStatus baseline = architectureIndex == -1 ? ArchitectureBaselineStatus.NotBound : ArchitectureBaselineStatus.Missing;
        void Error(ArchitectureDiagnosticCode code, string message, int? geoset = null, int? mesh = null)
            => diagnostics.Add(new(code, ArchitectureDiagnosticSeverity.Error, message, geoset, mesh));
        void Warning(ArchitectureDiagnosticCode code, string message)
            => diagnostics.Add(new(code, ArchitectureDiagnosticSeverity.Warning, message));
        ArchitectureBindingValidation Result() => new(index, architectureIndex, modelName,
            resolvedModel?.ResourceIdentity, resolvedModel?.RawSha256, compatible, baseline, diagnostics.AsReadOnly());

        if (architectureIndex == -1) return Result();
        if (level.Kind != ProjectType.MysteriesOfTheSith)
            Error(ArchitectureDiagnosticCode.UnsupportedProject, "Architectural lighting is a MotS binding.");
        ArchitecturalLightObject? architecture = null;
        if ((uint)architectureIndex >= level.ArchitecturalLighting.Count)
            Error(ArchitectureDiagnosticCode.MissingArchitectureObject, "Architectural-lighting object index is outside the stored section.");
        else architecture = level.ArchitecturalLighting[architectureIndex];
        try
        {
            if (!IsLevelGeometry(level, thing))
                Error(ArchitectureDiagnosticCode.IneligibleThingFlags, "Resolved thingflags lack level-geometry bit 0x8; this thing uses ordinary model lighting.");
        }
        catch (FormatException exception) { Error(ArchitectureDiagnosticCode.InvalidThingFlags, exception.Message); }
        if (string.IsNullOrEmpty(modelName) || modelName.Equals("none", StringComparison.OrdinalIgnoreCase))
            Error(ArchitectureDiagnosticCode.MissingModel, "Bound thing has no model3d.");
        if (checkStoredBaseline && !string.IsNullOrEmpty(thing.ArchLightingSourceModel) && !thing.ArchLightingSourceModel.Equals(modelName, StringComparison.OrdinalIgnoreCase))
            Error(ArchitectureDiagnosticCode.SourceModelNameMismatch, "Resolved model3d differs from the imported architectural-lighting source model.");
        if (resolvedModel is null)
            Error(ArchitectureDiagnosticCode.MissingModel, $"Model '{modelName}' is unavailable from the declared resources.");
        else if (!resolvedModel.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase))
            Error(ArchitectureDiagnosticCode.ResolvedModelNameMismatch, "The supplied resource identity does not resolve the thing's current model3d.");

        if (checkStoredBaseline && thing.ArchLightingProvenance is { } authored && resolvedModel is not null &&
            (!authored.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
             !authored.ModelSha256.Equals(resolvedModel.RawSha256, StringComparison.OrdinalIgnoreCase)))
            Error(ArchitectureDiagnosticCode.AuthoredModelBytesMismatch,
                "Current model bytes differ from the accepted architectural authoring resource. Explicitly rebind before exporting with these resources.");

        string sourceHash = checkStoredBaseline ? thing.ArchLightingSourceModelHash : string.Empty;
        if (string.IsNullOrEmpty(sourceHash))
            Warning(ArchitectureDiagnosticCode.MissingHistoricalBaseline, "No historical model fingerprint is stored; current topology compatibility does not certify the original bake.");
        else if (!IsSha256(sourceHash))
        {
            baseline = ArchitectureBaselineStatus.Unsupported;
            Error(ArchitectureDiagnosticCode.InvalidHistoricalFingerprint, "Stored historical fingerprint is not a SHA256 value.");
        }
        else if (resolvedModel?.Topology?.ArchitectureContentHash is not { } currentHash)
        {
            baseline = ArchitectureBaselineStatus.Unsupported;
            Error(ArchitectureDiagnosticCode.UnsupportedFingerprintNormalization,
                "The stored historical fingerprint cannot be compared with this resource's normalized text; this is unverified, not a demonstrated content mismatch.");
        }
        else if (!sourceHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
        {
            baseline = ArchitectureBaselineStatus.Mismatch;
            Error(ArchitectureDiagnosticCode.ModelContentMismatch, "Normalized model content differs from the stored architectural-lighting baseline.");
        }
        else baseline = ArchitectureBaselineStatus.Matches;

        if (resolvedModel is { Topology: null })
            Warning(ArchitectureDiagnosticCode.MissingTopology, "All-LOD topology is unavailable; this result cannot authorize the binding.");
        if (resolvedModel?.Topology is { } topology && architecture is not null)
        {
            if (!ValidShape(topology))
                Error(ArchitectureDiagnosticCode.InvalidTopology, "Topology must contain one to four sequential geometry sets within decoded count budgets.");
            else
            {
                compatible = true;
                IReadOnlyList<int> first = topology.Geosets[0].VertexCounts;
                if (first.Count != architecture.Meshes.Count)
                {
                    compatible = false;
                    Error(ArchitectureDiagnosticCode.TopLevelMeshMismatch, "Geoset zero mesh count differs from the architectural-lighting object.", 0);
                }
                for (int mesh = 0; mesh < System.Math.Min(first.Count, architecture.Meshes.Count); mesh++)
                    if (first[mesh] != architecture.Meshes[mesh].Vertices.Count)
                    {
                        compatible = false;
                        Error(ArchitectureDiagnosticCode.TopLevelVertexMismatch, "Geoset zero vertex count differs from the architectural-lighting mesh.", 0, mesh);
                    }
                for (int lod = 1; lod < topology.Geosets.Count; lod++)
                {
                    IReadOnlyList<int> shape = topology.Geosets[lod].VertexCounts;
                    if (shape.Count > architecture.Meshes.Count)
                    {
                        compatible = false;
                        Error(ArchitectureDiagnosticCode.LowerLodMeshOverflow, "Lower LOD requires more mesh slots than the architectural-lighting object provides.", lod);
                    }
                    for (int mesh = 0; mesh < System.Math.Min(shape.Count, architecture.Meshes.Count); mesh++)
                        if (shape[mesh] > architecture.Meshes[mesh].Vertices.Count)
                        {
                            compatible = false;
                            Error(ArchitectureDiagnosticCode.LowerLodVertexOverflow, "Lower LOD requires more vertex slots than the architectural-lighting mesh provides.", lod, mesh);
                        }
                }
            }
        }
        return Result();
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool ValidShape(ThreeDoTopology topology)
    {
        if (topology.Version != 2.1 && topology.Version != 2.2 || topology.Geosets is null ||
            topology.Geosets.Count < 1 || topology.Geosets.Count > ThreeDoTopologyReader.MaxGeosets) return false;
        long vertices = 0;
        foreach (var geometry in topology.Geosets)
        {
            if (geometry?.VertexCounts is null || geometry.VertexCounts.Count > ThreeDoTopologyReader.MaxMeshesPerGeoset) return false;
            foreach (int count in geometry.VertexCounts)
            {
                if (count < 0 || count > ThreeDoTopologyReader.MaxAggregateVertices) return false;
                vertices += count;
                if (vertices > ThreeDoTopologyReader.MaxAggregateVertices) return false;
            }
        }
        return true;
    }
}

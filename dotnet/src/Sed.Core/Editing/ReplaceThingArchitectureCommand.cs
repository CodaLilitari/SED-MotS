using Sed.Core.Lighting;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// Installs captured samples in a new object, leaving shared imported arrays
/// untouched. Undo restores the exact old binding; redo replays captured samples.
/// Resource verification belongs to preparation, outside this Core command.
/// </summary>
public sealed class ReplaceThingArchitectureCommand : IEditCommand
{
    private readonly Level _level;
    private readonly Thing _thing;
    private readonly ArchitecturalLightObject _samples;
    private readonly ArchitecturalLightObject[] _originalObjects;
    private readonly string _model;
    private readonly ArchitectureLightingProvenance _provenance;
    private readonly int _oldIndex;
    private readonly string _oldModel, _oldHash;
    private readonly ArchitectureLightingProvenance? _oldProvenance;
    private ArchitecturalLightObject? _installed;
    private bool _everApplied;

    public ReplaceThingArchitectureCommand(Level level, Thing thing, ArchitecturalLightObject samples,
        string model, ArchitectureLightingProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(samples); ArgumentNullException.ThrowIfNull(provenance);
        ArchitecturalLightingAuthoring.RequireEligibleModel(level, thing, model);
        if (!ArchitecturalLightingAuthoring.ValidProvenance(provenance)
            || !model.Equals(provenance.ModelName, StringComparison.OrdinalIgnoreCase)
            || !ArchitecturalLightingAuthoring.SamplesDigest(samples).Equals(provenance.SamplesSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Architectural lighting provenance does not describe the prepared samples and model.");
        if (provenance.Origin == ArchitecturalLightingAuthoring.BakeProfile
            && !ArchitecturalLightingAuthoring.InputsDigest(level, thing).Equals(provenance.InputSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Architectural lighting inputs changed before the samples were prepared.");
        if (thing.ArchLightingIndex < -1 || thing.ArchLightingIndex >= level.ArchitecturalLighting.Count)
            throw new InvalidOperationException("The previous architectural lighting binding is outside the stored objects.");
        _level = level; _thing = thing; _model = model; _provenance = provenance;
        _samples = samples.Clone(); _originalObjects = level.ArchitecturalLighting.ToArray();
        _oldIndex = thing.ArchLightingIndex; _oldModel = thing.ArchLightingSourceModel;
        _oldHash = thing.ArchLightingSourceModelHash; _oldProvenance = thing.ArchLightingProvenance;
    }

    public string Name => _provenance.Origin == ArchitecturalLightingAuthoring.BakeProfile
        ? "Bake architectural source-point lighting" : "Replace architectural lighting binding";

    public void Apply()
    {
        ArchitecturalLightingAuthoring.RequireEligibleModel(_level, _thing, _model);
        if (_installed is not null || !OriginalPrefixMatches() || _level.ArchitecturalLighting.Count != _originalObjects.Length
            || _thing.ArchLightingIndex != _oldIndex || _thing.ArchLightingSourceModel != _oldModel
            || _thing.ArchLightingSourceModelHash != _oldHash || _thing.ArchLightingProvenance != _oldProvenance)
            throw new InvalidOperationException("Architectural lighting dependencies changed outside undo history; apply was refused before changing data.");
        if (!_everApplied && _provenance.Origin == ArchitecturalLightingAuthoring.BakeProfile
            && !ArchitecturalLightingAuthoring.InputsDigest(_level, _thing).Equals(_provenance.InputSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Architectural lighting inputs changed after preparation; prepare the bake again.");
        var installed = _samples.Clone();
        _level.ArchitecturalLighting.Add(installed);
        _thing.ArchLightingIndex = _originalObjects.Length;
        _thing.ArchLightingSourceModel = _model;
        _thing.ArchLightingSourceModelHash = string.Empty;
        _thing.ArchLightingProvenance = _provenance;
        _installed = installed; _everApplied = true;
    }

    public void Revert()
    {
        if (_installed is null) return;
        ArchitecturalLightingAuthoring.RequireLiveThing(_level, _thing);
        if (!OriginalPrefixMatches() || _level.ArchitecturalLighting.Count != _originalObjects.Length + 1
            || !ReferenceEquals(_level.ArchitecturalLighting[^1], _installed)
            || _thing.ArchLightingIndex != _originalObjects.Length || _thing.ArchLightingSourceModel != _model
            || _thing.ArchLightingSourceModelHash != string.Empty || _thing.ArchLightingProvenance != _provenance
            || _level.Things.Any(other => other != _thing && other.ArchLightingIndex == _originalObjects.Length)
            || !ArchitecturalLightingAuthoring.SamplesDigest(_installed).Equals(_provenance.SamplesSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Architectural lighting dependencies changed outside undo history; undo was refused before removing data.");
        _thing.ArchLightingIndex = _oldIndex; _thing.ArchLightingSourceModel = _oldModel;
        _thing.ArchLightingSourceModelHash = _oldHash; _thing.ArchLightingProvenance = _oldProvenance;
        _level.ArchitecturalLighting.RemoveAt(_originalObjects.Length);
        _installed = null;
    }

    private bool OriginalPrefixMatches() => _level.ArchitecturalLighting.Count >= _originalObjects.Length
        && _originalObjects.Select((item, index) => ReferenceEquals(item, _level.ArchitecturalLighting[index])).All(matches => matches);
}

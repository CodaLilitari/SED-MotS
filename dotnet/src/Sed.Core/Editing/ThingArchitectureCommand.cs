using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>Explicitly unbinds old model lighting while retaining the archived arrays for undo/reuse.</summary>
public sealed class ClearThingArchitectureCommand : IEditCommand
{
    private readonly Thing _thing;
    private readonly int _index;
    private readonly string _model, _hash;
    private readonly ArchitectureLightingProvenance? _provenance;
    public ClearThingArchitectureCommand(Thing thing)
    {
        _thing = thing; _index = thing.ArchLightingIndex; _model = thing.ArchLightingSourceModel;
        _hash = thing.ArchLightingSourceModelHash; _provenance = thing.ArchLightingProvenance;
    }
    public string Name => "Unbind architectural lighting";
    public void Apply()
    {
        _thing.ArchLightingIndex = -1; _thing.ArchLightingSourceModel = string.Empty;
        _thing.ArchLightingSourceModelHash = string.Empty; _thing.ArchLightingProvenance = null;
    }
    public void Revert()
    {
        _thing.ArchLightingIndex = _index; _thing.ArchLightingSourceModel = _model;
        _thing.ArchLightingSourceModelHash = _hash; _thing.ArchLightingProvenance = _provenance;
    }
}

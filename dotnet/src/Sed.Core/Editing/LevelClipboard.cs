using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>
/// A detached snapshot of part of a level — the editor's clipboard (mirrors
/// `U_COPYPASTE.PAS`). Capturing deep-copies the selection, so later edits to the
/// original do not disturb what was copied, and each paste clones the snapshot
/// again so repeated pastes produce independent geometry.
///
/// JK shares world vertices between sectors, so cloning always creates fresh
/// <see cref="Vertex"/> objects rather than aliasing the source. Adjoins are
/// remapped when both sides of the pair are inside the fragment and cleared
/// otherwise — a pasted room must not stay portal-linked to the room it came from.
/// </summary>
public sealed class LevelFragment
{
    private readonly List<Sector> _sectors = new();
    private readonly List<Thing> _things = new();
    private readonly List<Light> _lights = new();
    private readonly List<ArchitecturalLightObject> _architecturalObjects = new();
    private readonly Dictionary<int, string> _layers = new();
    private readonly Dictionary<string, Template> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingTemplates = new(StringComparer.OrdinalIgnoreCase);
    private Level? _sourceLevel;
    private string? _captureError;
    public ProjectType Game { get; private set; }
    public string? CompatibilityError(Level target)
    {
        if (target.Kind != Game)
            return "This clipboard belongs to a different game. Cross-game conversion is not supported; paste into a level for the same game to preserve its lighting and parameters.";
        if (_captureError is not null) return _captureError;
        foreach (string name in _missingTemplates)
            if (!ReferenceEquals(target, _sourceLevel) || target.Templates.ContainsKey(name))
                return $"Clipboard template '{name}' was undefined when copied. Resolve its source definition before copying it into another document or changed template library.";
        foreach (var (name, source) in _templates)
            if (target.Templates.TryGetValue(name, out var existing))
            {
                if (!SameTemplate(source, existing))
                    return $"Target template '{name}' differs from the clipboard snapshot. Paste was refused to preserve the thing's inherited parameters.";
                if (!NoParent(source.Parent) && !target.Templates.ContainsKey(source.Parent) && _templates.ContainsKey(source.Parent))
                    return $"Target template '{name}' has a missing parent '{source.Parent}'. Appending that parent after its existing child would change engine template loading order.";
            }
        long nextOrder = target.Templates.Values.Select(t => (long)t.Order).DefaultIfEmpty(-1).Max() + 1;
        if (nextOrder + _templates.Keys.Count(name => !target.Templates.ContainsKey(name)) > int.MaxValue)
            return "Target template declaration order cannot accommodate the clipboard definitions.";
        return null;
    }

    public IReadOnlyList<Sector> Sectors => _sectors;
    public IReadOnlyList<Thing> Things => _things;
    public IReadOnlyList<Light> Lights => _lights;

    public bool IsEmpty => _sectors.Count == 0 && _things.Count == 0 && _lights.Count == 0;

    /// <summary>Total item count, for status messages.</summary>
    public int Count => _sectors.Count + _things.Count + _lights.Count;

    /// <summary>World-space extent of the captured contents.</summary>
    public Box Bounds
    {
        get
        {
            var box = Box.Empty;
            foreach (var s in _sectors)
                foreach (var v in s.Vertices) box.Encapsulate(v.Position);
            foreach (var t in _things) box.Encapsulate(t.Position);
            foreach (var l in _lights) box.Encapsulate(l.Position);
            return box;
        }
    }

    /// <summary>
    /// Snapshots the current selection. Selected sectors are taken whole; selected
    /// surfaces contribute their owning sector, since a surface cannot exist
    /// outside one. Returns an empty fragment when nothing copyable is selected.
    /// </summary>
    public static LevelFragment Capture(SelectionSet selection, Level level)
    {
        var fragment = new LevelFragment { Game = level.Kind, _sourceLevel = level };

        // Sectors: explicitly selected, plus those owning any selected surface.
        var wanted = new List<Sector>();
        var seen = new HashSet<Sector>();
        foreach (var s in selection.Sectors)
            if (seen.Add(s)) wanted.Add(s);
        foreach (var surf in selection.Surfaces)
            if (seen.Add(surf.Sector)) wanted.Add(surf.Sector);

        var surfaceMap = new Dictionary<Surface, Surface>();
        var sectorMap = new Dictionary<Sector, Sector>();

        foreach (var src in wanted)
        {
            var clone = CloneSector(src, level, Vec3.Zero, surfaceMap);
            sectorMap[src] = clone;
            fragment._sectors.Add(clone);
        }
        RemapAdjoins(surfaceMap);

        var lightMap = new Dictionary<int, int>();
        foreach (var t in selection.Things)
        {
            var clone = CloneThing(t, level, Vec3.Zero, sectorMap);
            if (t.ArchLightingIndex >= 0 && t.ArchLightingIndex < level.ArchitecturalLighting.Count)
            {
                if (!lightMap.TryGetValue(t.ArchLightingIndex, out int mapped))
                {
                    mapped = fragment._architecturalObjects.Count;
                    lightMap[t.ArchLightingIndex] = mapped;
                    fragment._architecturalObjects.Add(level.ArchitecturalLighting[t.ArchLightingIndex].Clone());
                }
                clone.ArchLightingIndex = mapped;
            }
            else clone.ArchLightingIndex = -1;
            fragment._things.Add(clone);
        }

        foreach (var l in selection.Lights)
            fragment._lights.Add(CloneLight(l, Vec3.Zero));

        foreach (int layer in fragment._sectors.Select(s => s.Layer).Concat(fragment._things.Select(t => t.Layer))
            .Concat(fragment._lights.Select(l => l.Layer)).Distinct().Order())
        {
            if ((uint)layer < (uint)level.Layers.Count) fragment._layers[layer] = level.Layers[layer];
            else if (layer == 0 && level.Layers.Count == 0) fragment._layers[layer] = "Layer0";
            else fragment._captureError ??= $"Clipboard references missing source layer {layer}. Repair the source layer assignment before copying.";
        }
        foreach (var thing in fragment._things) fragment.CaptureTemplateChain(thing.Template, level);

        return fragment;
    }

    private void CaptureTemplateChain(string name, Level source)
    {
        var chain = new List<Template>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!NoParent(name) && !_templates.ContainsKey(name))
        {
            if (!visited.Add(name))
            { _captureError ??= $"Clipboard template inheritance contains a cycle at '{name}'. Repair the source templates before copying."; return; }
            if (!source.Templates.TryGetValue(name, out var template))
            { _missingTemplates.Add(name); break; }
            chain.Add(template);
            name = template.Parent;
        }
        // Engines construct inherited templates in declaration order.
        for (int i = chain.Count - 1; i >= 0; i--)
            _templates.Add(chain[i].Name, CloneTemplate(chain[i]));
    }

    private static bool NoParent(string name) => string.IsNullOrWhiteSpace(name) || name.Equals("none", StringComparison.OrdinalIgnoreCase);
    private static Template CloneTemplate(Template source)
    {
        var copy = new Template { Name = source.Name, Parent = source.Parent, Order = source.Order };
        foreach (var pair in source.Values) copy.Values.Add(pair.Key, pair.Value);
        copy.SourceParameters.AddRange(source.SourceParameters);
        return copy;
    }

    private static bool SameTemplate(Template source, Template target) =>
        (NoParent(source.Parent) && NoParent(target.Parent) || source.Parent.Equals(target.Parent, StringComparison.OrdinalIgnoreCase)) &&
        source.Values.Count == target.Values.Count && source.Values.All(pair => target.Values.TryGetValue(pair.Key, out string? value) && pair.Value == value) &&
        source.SourceParameters.Count == target.SourceParameters.Count && source.SourceParameters.Zip(target.SourceParameters).All(pair =>
            pair.First.Key.Equals(pair.Second.Key, StringComparison.OrdinalIgnoreCase) && pair.First.Value == pair.Second.Value);

    internal (Dictionary<int, int> layerMap, List<string> addedLayers, List<Template> addedTemplates) PrepareDependencies(Level target)
    {
        var names = target.Layers.ToList();
        var assigned = new HashSet<int>();
        var map = new Dictionary<int, int>();
        foreach (var (sourceIndex, name) in _layers)
        {
            int index = (uint)sourceIndex < (uint)names.Count && names[sourceIndex] == name && !assigned.Contains(sourceIndex)
                ? sourceIndex : names.FindIndex(0, names.Count, candidate => candidate == name);
            if (index >= 0 && assigned.Contains(index))
                index = Enumerable.Range(0, names.Count).FirstOrDefault(i => !assigned.Contains(i) && names[i] == name, -1);
            if (index < 0) { index = names.Count; names.Add(name); }
            assigned.Add(index); map.Add(sourceIndex, index);
        }
        int order = checked((int)(target.Templates.Values.Select(t => (long)t.Order).DefaultIfEmpty(-1).Max() + 1));
        var templates = new List<Template>();
        foreach (var (name, source) in _templates)
            if (!target.Templates.ContainsKey(name))
            { var copy = CloneTemplate(source); copy.Order = order++; templates.Add(copy); }
        return (map, names.Skip(target.Layers.Count).ToList(), templates);
    }

    /// <summary>
    /// Clones this fragment's contents into <paramref name="target"/>, displaced by
    /// <paramref name="offset"/>. The objects are created but <em>not</em> added to
    /// the level — <see cref="PasteFragmentCommand"/> does that so paste is undoable.
    /// </summary>
    internal (List<Sector> sectors, List<Thing> things, List<Light> lights,
        List<ArchitecturalLightObject> architecturalObjects) Instantiate(Level target, Vec3 offset, IReadOnlyDictionary<int, int> layerMap)
    {
        var surfaceMap = new Dictionary<Surface, Surface>();
        var sectorMap = new Dictionary<Sector, Sector>();

        var sectors = new List<Sector>(_sectors.Count);
        foreach (var src in _sectors)
        {
            var clone = CloneSector(src, target, offset, surfaceMap);
            clone.Layer = layerMap[src.Layer];
            sectorMap[src] = clone;
            sectors.Add(clone);
        }
        RemapAdjoins(surfaceMap);

        var things = new List<Thing>(_things.Count);
        foreach (var t in _things)
        {
            var clone = CloneThing(t, target, offset, sectorMap);
            clone.Layer = layerMap[t.Layer];
            clone.ArchLightingIndex = t.ArchLightingIndex < 0 ? -1 : target.ArchitecturalLighting.Count + t.ArchLightingIndex;
            things.Add(clone);
        }

        var lights = new List<Light>(_lights.Count);
        foreach (var l in _lights)
        { var clone = CloneLight(l, offset); clone.Layer = layerMap[l.Layer]; lights.Add(clone); }

        return (sectors, things, lights, _architecturalObjects.Select(o => o.Clone()).ToList());
    }

    private static Light CloneLight(Light src, Vec3 offset) => new()
    {
        Position = src.Position + offset,
        Range = src.Range,
        Intensity = src.Intensity,
        Color = src.Color,
        Flags = src.Flags,
        Layer = src.Layer,
    };

    private static Sector CloneSector(Sector src, Level target, Vec3 offset,
        Dictionary<Surface, Surface> surfaceMap)
    {
        var dst = new Sector(target)
        {
            Flags = src.Flags,
            Ambient = src.Ambient,
            ExtraLight = src.ExtraLight,
            Tint = src.Tint,
            ColorMap = src.ColorMap,
            Sound = src.Sound,
            SoundVolume = src.SoundVolume,
            Thrust = src.Thrust,
            Layer = src.Layer,
            HasCollideBox = src.HasCollideBox,
            CollideBox = new Box(src.CollideBox.Min + offset, src.CollideBox.Max + offset),
            BoundBox = src.BoundBox is Box bounds ? new Box(bounds.Min + offset, bounds.Max + offset) : null,
            Center = src.Center is Vec3 center ? center + offset : null,
            Radius = src.Radius,
            BoundsSourceVertices = src.BoundsSourceVertices?.Select(v => v + offset).ToArray(),
        };

        var vertexMap = new Dictionary<Vertex, Vertex>();
        foreach (var v in src.Vertices)
            vertexMap[v] = dst.AddVertex(v.Position + offset);

        foreach (var s in src.Surfaces)
        {
            var ns = dst.NewSurface();
            ns.Material = s.Material;
            ns.MaterialIndex = s.MaterialIndex;
            ns.SurfFlags = s.SurfFlags;
            ns.FaceFlags = s.FaceFlags;
            ns.Geo = s.Geo;
            ns.Light = s.Light;
            ns.Tex = s.Tex;
            ns.ExtraLightIntensity = s.ExtraLightIntensity;
            ns.UScale = s.UScale;
            ns.VScale = s.VScale;
            ns.AdjoinFlags = s.AdjoinFlags;

            foreach (var c in s.Corners)
            {
                // A surface should only reference its own sector's vertices, but
                // clone defensively so a malformed level cannot alias the source.
                if (!vertexMap.TryGetValue(c.Vertex, out var nv))
                    nv = vertexMap[c.Vertex] = dst.AddVertex(c.Vertex.Position + offset);

                ns.Corners.Add(new Surface.Corner { Vertex = nv, Uv = c.Uv, Intensity = c.Intensity, MonoIntensity = c.MonoIntensity });
            }

            ns.RecalcNormal();
            surfaceMap[s] = ns;
        }

        dst.Renumber();
        return dst;
    }

    /// <summary>
    /// Rebuilds adjoin links among cloned surfaces. Pairs wholly inside the clone
    /// set are re-pointed at their clones; anything pointing outside is cleared,
    /// so a pasted room is sealed rather than opening into the original.
    /// </summary>
    private static void RemapAdjoins(Dictionary<Surface, Surface> surfaceMap)
    {
        foreach (var (src, dst) in surfaceMap)
        {
            if (src.Adjoin is { } partner && surfaceMap.TryGetValue(partner, out var clonedPartner))
            {
                dst.Adjoin = clonedPartner;
            }
            else
            {
                dst.Adjoin = null;
                dst.AdjoinFlags = 0;
            }
        }
    }

    private static Thing CloneThing(Thing src, Level target, Vec3 offset,
        Dictionary<Sector, Sector> sectorMap)
    {
        var dst = new Thing
        {
            Template = src.Template,
            Name = src.Name,
            Position = src.Position + offset,
            Pitch = src.Pitch,
            Yaw = src.Yaw,
            Roll = src.Roll,
            Layer = src.Layer,
            Flags = src.Flags,
            Level = target,
            ArchLightingIndex = src.ArchLightingIndex,
            ArchLightingSourceModel = src.ArchLightingSourceModel,
            ArchLightingSourceModelHash = src.ArchLightingSourceModelHash,
        };

        foreach (var (k, v) in src.Values) dst.Values[k] = v;
        dst.SourceParameters.AddRange(src.SourceParameters);

        // Keep the thing inside its copied room when that room came along;
        // A cross-level paste cannot retain a pointer into another document.
        dst.Sector = src.Sector is { } sec && sectorMap.TryGetValue(sec, out var mapped) ? mapped :
            src.Sector is { } original && ReferenceEquals(original.Level, target) && target.Sectors.Contains(original) ? original : null;
        return dst;
    }
}

/// <summary>
/// Pastes a <see cref="LevelFragment"/> into a level, displaced by an offset.
/// Reversible; redo re-adds the same objects rather than making new ones, so undo
/// history stays stable.
/// </summary>
public sealed class PasteFragmentCommand : IEditCommand
{
    private readonly Level _level;
    private readonly LevelFragment _fragment;
    private readonly Vec3 _offset;

    private List<Sector>? _sectors;
    private List<Thing>? _things;
    private List<Light>? _lights;
    private List<ArchitecturalLightObject>? _architecturalObjects;
    private int _architectureStart;
    private int _layerStart;
    private List<string>? _addedLayers;
    private List<Template>? _addedTemplates;
    private string[]? _baseLayers;
    private string[]? _baseColorMaps;
    private List<string>? _addedColorMaps;

    public PasteFragmentCommand(Level level, LevelFragment fragment, Vec3 offset)
    {
        _level = level;
        _fragment = fragment;
        _offset = offset;
    }

    public string Name => $"Paste {_fragment.Count} item(s)";

    /// <summary>Objects this paste created — the editor selects them afterwards.</summary>
    public IReadOnlyList<Sector> PastedSectors => _sectors ?? (IReadOnlyList<Sector>)Array.Empty<Sector>();
    public IReadOnlyList<Thing> PastedThings => _things ?? (IReadOnlyList<Thing>)Array.Empty<Thing>();
    public IReadOnlyList<Light> PastedLights => _lights ?? (IReadOnlyList<Light>)Array.Empty<Light>();

    public void Apply()
    {
        if (_fragment.CompatibilityError(_level) is { } error) throw new InvalidOperationException(error);
        if (_sectors is null || _things is null || _lights is null)
        {
            _architectureStart = _level.ArchitecturalLighting.Count;
            _layerStart = _level.Layers.Count;
            _baseLayers = _level.Layers.ToArray();
            _baseColorMaps = _level.ColorMaps.ToArray();
            var dependencies = _fragment.PrepareDependencies(_level);
            (_sectors, _things, _lights, _architecturalObjects) = _fragment.Instantiate(_level, _offset, dependencies.layerMap);
            _addedLayers = dependencies.addedLayers;
            _addedTemplates = dependencies.addedTemplates;
            _addedColorMaps = _sectors.Select(sector => sector.ColorMap).Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Where(name => !_level.ColorMaps.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        if (!_level.Layers.SequenceEqual(_baseLayers!) || !_level.ColorMaps.SequenceEqual(_baseColorMaps!) ||
            _level.ArchitecturalLighting.Count != _architectureStart ||
            _addedTemplates!.Any(template => _level.Templates.ContainsKey(template.Name)))
            throw new InvalidOperationException("Paste dependencies changed outside undo history; create a new paste command.");
        _level.Layers.AddRange(_addedLayers!);
        _level.ColorMaps.AddRange(_addedColorMaps!);
        foreach (var template in _addedTemplates!) _level.Templates.Add(template.Name, template);
        _level.ArchitecturalLighting.AddRange(_architecturalObjects!);
        foreach (var s in _sectors) _level.Sectors.Add(s);
        foreach (var t in _things) _level.Things.Add(t);
        foreach (var l in _lights) _level.Lights.Add(l);

        _level.RenumberSectors();
        _level.RenumberThings();
        _level.RenumberLights();
    }

    public void Revert()
    {
        if (_sectors is null || _things is null || _lights is null) return;

        if (_level.ArchitecturalLighting.Count != _architectureStart + _architecturalObjects!.Count ||
            !_level.Layers.SequenceEqual(_baseLayers!.Concat(_addedLayers!)) ||
            !_level.ColorMaps.SequenceEqual(_baseColorMaps!.Concat(_addedColorMaps!)) ||
            _addedTemplates!.Any(template => !_level.Templates.TryGetValue(template.Name, out var present) || !ReferenceEquals(template, present)))
            throw new InvalidOperationException("Paste dependencies changed outside undo history; undo was refused before removing data.");

        foreach (var s in _sectors) _level.Sectors.Remove(s);
        foreach (var t in _things) _level.Things.Remove(t);
        foreach (var l in _lights) _level.Lights.Remove(l);
        _level.ArchitecturalLighting.RemoveRange(_architectureStart, _architecturalObjects!.Count);
        foreach (var template in _addedTemplates!) _level.Templates.Remove(template.Name);
        _level.Layers.RemoveRange(_layerStart, _addedLayers!.Count);
        _level.ColorMaps.RemoveRange(_baseColorMaps!.Length, _addedColorMaps!.Count);

        _level.RenumberSectors();
        _level.RenumberThings();
        _level.RenumberLights();
    }
}

using System.Globalization;
using Sed.Core.Math;
using Sed.Core.Model;
using Sed.Core.Query;

namespace Sed.Core.Editing;

/// <summary>
/// Appends fresh template instances after validating every placement. Does not
/// clone placed things, COG bindings, instance values or ARCH. Existing objects,
/// numbers and declared capacity are unchanged; undo removes the exact suffix.
/// </summary>
public sealed class CreateThingArrayCommand : IEditCommand
{
    private readonly Level _level;
    private readonly IReadOnlyList<Vec3> _positions;
    private readonly Sector? _sector;
    private readonly Thing[] _things;
    private readonly ThingPayload[] _payloads;
    private readonly Action? _validateReferenceClosure;
    private int _start;
    private bool _applied;
    public IReadOnlyList<Thing> Things { get; }
    public string Name => "Create thing array";

    public CreateThingArrayCommand(Level level, IReadOnlyList<Vec3> positions, string template,
        string namePrefix, Sector? sector = null, Vec3 orientation = default, int layer = 0,
        Action? validateReferenceClosure = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        PlacementArrayRules.RequireGame(level);
        PlacementArrayRules.RequireToken(template, 127, nameof(template));
        PlacementArrayRules.RequireToken(namePrefix, 123, nameof(namePrefix));
        PlacementArrayLayout.RequireVector(orientation, nameof(orientation));
        PlacementArrayRules.RequireLayer(level, layer);
        _level = level; _positions = PlacementArrayLayout.CheckedPositions(positions); _sector = sector;
        _validateReferenceClosure = validateReferenceClosure;
        _things = _positions.Select((point, i) => new Thing {
            Level = level, Position = point, Template = template,
            Name = namePrefix + "_" + i.ToString("D3", CultureInfo.InvariantCulture),
            Pitch = orientation.X, Yaw = orientation.Y, Roll = orientation.Z, Layer = layer,
        }).ToArray();
        _payloads = _things.Select(ThingPayload.Of).ToArray();
        Things = Array.AsReadOnly(_things);
        Validate();
    }

    private Sector[] Validate()
    {
        PlacementArrayRules.RequireGame(_level);
        if (_level.Cogs.Count > 0 && _validateReferenceClosure is null)
            throw new InvalidOperationException("Appending things to a level with placed COGs requires complete COG schemas to verify existing thing references.");
        _validateReferenceClosure?.Invoke();
        for (int i = 0; i < _things.Length; i++)
            if (ThingPayload.Of(_things[i]) != _payloads[i] || !ReferenceEquals(_things[i].Level, _level)
                || _things[i].ArchLightingIndex != -1 || _things[i].ArchLightingSourceModel != string.Empty
                || _things[i].ArchLightingSourceModelHash != string.Empty || _things[i].ArchLightingProvenance is not null
                || _things[i].Values.Count != 0 || _things[i].SourceParameters.Count != 0
                || _things[i].Flags != 0 || _things[i].Id != 0 || !_things[i].BBox.Equals(default(Box)))
                throw new InvalidOperationException($"Pending thing array payload {i} changed after preparation; prepare a new command.");
        if (!_level.Templates.ContainsKey(_things[0].Template))
            throw new ArgumentException("Template is not defined in this level.");
        if (_level.ThingCapacity < _level.Things.Count || _things.Length > _level.ThingCapacity - _level.Things.Count)
            throw new InvalidOperationException("The array exceeds the declared thing capacity. Array placement does not grow capacity or reserve runtime spawning headroom.");
        if (_sector is not null && !_level.Sectors.Contains(_sector))
            throw new ArgumentException("The explicit sector must belong to this level.");
        var names = _level.Things.Select(thing => thing.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var thing in _things)
        {
            PlacementArrayRules.RequireLayer(_level, thing.Layer);
            if (!names.Add(thing.Name)) throw new InvalidOperationException($"Generated thing name '{thing.Name}' already exists (case-insensitive).");
        }
        var sectors = new Sector[_positions.Count];
        for (int i = 0; i < _positions.Count; i++)
        {
            var matches = SpatialSelection.SelectSectors(_level, _positions[i]);
            if (_sector is not null)
            {
                var match = matches.SingleOrDefault(candidate => ReferenceEquals(candidate.Sector, _sector));
                if (match is null || match.OnBoundary)
                    throw new InvalidOperationException($"Array position {i} must be strictly inside the explicit sector, away from its boundary.");
                sectors[i] = _sector;
            }
            else
            {
                if (matches.Count != 1 || matches[0].OnBoundary)
                    throw new InvalidOperationException($"Array position {i} requires exactly one containing sector and cannot lie on a sector boundary. Supply an explicit sector to resolve intentional overlaps.");
                sectors[i] = matches[0].Sector;
            }
        }
        return sectors;
    }

    private readonly record struct ThingPayload(string Name, string Template, Vec3 Position, Vec3 Orientation, int Layer)
    {
        public static ThingPayload Of(Thing thing) => new(thing.Name, thing.Template, thing.Position,
            new(thing.Pitch, thing.Yaw, thing.Roll), thing.Layer);
    }

    public void Apply()
    {
        if (_applied) throw new InvalidOperationException("The array is already applied.");
        var sectors = Validate(); // Finish every fallible semantic check before changing the level.
        _start = _level.Things.Count;
        _level.Things.EnsureCapacity(checked(_start + _things.Length));
        for (int i = 0; i < _things.Length; i++) { _things[i].Num = _start + i; _things[i].Sector = sectors[i]; }
        _level.Things.AddRange(_things);
        _applied = true;
    }

    public void Revert()
    {
        if (!_applied) throw new InvalidOperationException("The array is not applied.");
        PlacementArrayRules.RequireSuffix(_level.Things, _things, _start);
        _level.Things.RemoveRange(_start, _things.Length);
        _applied = false;
    }
}

/// <summary>Appends independent authoring lights, preserving null versus explicit RGB intensity.</summary>
public sealed class CreateLightArrayCommand : IEditCommand
{
    private readonly Level _level;
    private readonly Light[] _lights;
    private readonly LightPayload[] _payloads;
    private readonly ProjectType _game;
    private int _start;
    private bool _applied;
    public IReadOnlyList<Light> Lights { get; }
    public string Name => "Create source light array";

    public CreateLightArrayCommand(Level level, IReadOnlyList<Vec3> positions, ColorF rgb, double intensity,
        double range, double? rgbIntensity = null, double? legacyRgbRange = null, long flags = 0, int layer = 0)
    {
        ArgumentNullException.ThrowIfNull(level);
        PlacementArrayRules.RequireGame(level);
        PlacementArrayRules.RequireLayer(level, layer);
        if (new[] { rgb.R, rgb.G, rgb.B }.Any(value => !float.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(rgb), "RGB components must be finite and in [0,1].");
        if (!double.IsFinite(intensity) || intensity is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(intensity));
        if (!double.IsFinite(range) || range is < 0.0001 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(range));
        if (rgbIntensity is { } independent && (!double.IsFinite(independent) || independent is < 0 or > 1000))
            throw new ArgumentOutOfRangeException(nameof(rgbIntensity));
        if (legacyRgbRange is { } metadata && (!double.IsFinite(metadata) || metadata is < -100000 or > 100000))
            throw new ArgumentOutOfRangeException(nameof(legacyRgbRange));
        if (flags < 0 || flags > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(flags));
        if (level.Kind != ProjectType.MysteriesOfTheSith && (rgb != ColorF.White || rgbIntensity.HasValue))
            throw new ArgumentException("Colored or independent RGB source-light authoring requires MotS.");
        _level = level; _game = level.Kind;
        _lights = PlacementArrayLayout.CheckedPositions(positions).Select(point => new Light {
            Position = point, Color = rgb, Intensity = intensity, Range = range, RgbIntensity = rgbIntensity,
            LegacyRgbRange = legacyRgbRange, Flags = flags, Layer = layer,
        }).ToArray();
        _payloads = _lights.Select(LightPayload.Of).ToArray();
        Lights = Array.AsReadOnly(_lights);
    }

    public void Apply()
    {
        if (_applied) throw new InvalidOperationException("The array is already applied.");
        if (_level.Kind != _game) throw new InvalidOperationException("The level game changed after array preparation; prepare a new command.");
        for (int i = 0; i < _lights.Length; i++)
            if (LightPayload.Of(_lights[i]) != _payloads[i])
                throw new InvalidOperationException($"Pending light array payload {i} changed after preparation; prepare a new command.");
        foreach (var light in _lights) PlacementArrayRules.RequireLayer(_level, light.Layer);
        _start = _level.Lights.Count;
        _level.Lights.EnsureCapacity(checked(_start + _lights.Length));
        for (int i = 0; i < _lights.Length; i++) _lights[i].Num = _start + i;
        _level.Lights.AddRange(_lights);
        _applied = true;
    }

    public void Revert()
    {
        if (!_applied) throw new InvalidOperationException("The array is not applied.");
        PlacementArrayRules.RequireSuffix(_level.Lights, _lights, _start);
        _level.Lights.RemoveRange(_start, _lights.Length);
        _applied = false;
    }

    private readonly record struct LightPayload(Vec3 Position, ColorF Color, double Intensity, double Range,
        double? RgbIntensity, double? LegacyRgbRange, long Flags, int Layer, int Id)
    {
        public static LightPayload Of(Light light) => new(light.Position, light.Color, light.Intensity, light.Range,
            light.RgbIntensity, light.LegacyRgbRange, light.Flags, light.Layer, light.Id);
    }
}

internal static class PlacementArrayRules
{
    internal static void RequireToken(string text, int maximum, string parameter)
    {
        if (string.IsNullOrEmpty(text) || text.Length > maximum || text.Any(c => c <= 32 || c >= 127 || c is '#' or '"'))
            throw new ArgumentException($"Use a nonempty ASCII token of at most {maximum} characters without whitespace, quotes or comments.", parameter);
    }

    internal static void RequireGame(Level level)
    {
        if (level.Kind is not (ProjectType.JediKnight or ProjectType.MysteriesOfTheSith))
            throw new ArgumentException("Array authoring supports JK and MotS levels.");
    }

    internal static void RequireLayer(Level level, int layer)
    {
        if (layer < 0 || layer >= System.Math.Max(1, level.Layers.Count))
            throw new ArgumentOutOfRangeException(nameof(layer), "Use an existing layer; zero is the default when no layers are named.");
    }

    internal static void RequireSuffix<T>(List<T> list, T[] added, int start) where T : class
    {
        if (list.Count != start + added.Length || added.Where((item, i) => !ReferenceEquals(item, list[start + i])).Any())
            throw new InvalidOperationException("Undo requires the unchanged appended array suffix; undo later edits first.");
    }
}

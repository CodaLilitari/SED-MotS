using System.Globalization;
using Sed.Core.Math;
using Sed.Core.Model;

namespace Sed.Core.Editing;

/// <summary>An absolute path position and pitch/yaw/roll, in engine degrees.</summary>
public readonly record struct ThingFrame(Vec3 Position, Vec3 Orientation)
{
    public string ToParameterValue()
    {
        static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        return $"({F(Position.X)}/{F(Position.Y)}/{F(Position.Z)}:{F(Orientation.X)}/{F(Orientation.Y)}/{F(Orientation.Z)})";
    }
}

/// <summary>
/// Replaces one path mover's ordered frame records and their dictionary projection.
/// Unrelated imported records retain their values and relative order; undo restores
/// both original representations exactly. This does not simulate movement/collision.
/// </summary>
public sealed class SetThingFramesCommand : IEditCommand
{
    public const int MaxFrames = 256;
    public const double MaxCoordinate = 1_000_000;
    private readonly Thing _thing;
    private readonly KeyValuePair<string, string>[] _oldRecords, _oldValues, _newRecords;
    private readonly string[] _frames;

    public SetThingFramesCommand(Level level, Thing thing, IReadOnlyList<ThingFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(thing);
        ArgumentNullException.ThrowIfNull(frames);
        if (!level.Things.Contains(thing)) throw new ArgumentException("The thing must belong to this level.", nameof(thing));
        if (level.Kind is not (ProjectType.JediKnight or ProjectType.MysteriesOfTheSith))
            throw new ArgumentException("Path frame authoring supports JK and MotS levels.", nameof(level));
        if (frames.Count is < 1 or > MaxFrames)
            throw new ArgumentException($"Use 1 through {MaxFrames} frames; this is an editor work limit, not an engine capacity claim.", nameof(frames));
        string move = thing.Values.TryGetValue("move", out var direct) ? direct : level.GetTemplateValue(thing.Template, "move");
        if (!move.Equals("path", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Frame authoring requires an explicit or inherited move=path thing.", nameof(thing));
        if (thing.ArchLightingIndex >= 0)
            throw new ArgumentException("Clear the architectural lighting binding explicitly before authoring a moving path.", nameof(thing));
        foreach (var frame in frames)
            foreach (double value in new[] { frame.Position.X, frame.Position.Y, frame.Position.Z,
                frame.Orientation.X, frame.Orientation.Y, frame.Orientation.Z })
                if (!double.IsFinite(value) || System.Math.Abs(value) > MaxCoordinate)
                    throw new ArgumentException($"Frame coordinates and angles must be finite and within +/-{MaxCoordinate}.", nameof(frames));

        _thing = thing;
        _oldRecords = thing.SourceParameters.ToArray();
        _oldValues = thing.Values.ToArray();
        _frames = frames.Select(f => f.ToParameterValue()).ToArray();
        var records = new List<KeyValuePair<string, string>>();
        int insertion = -1;
        foreach (var record in _oldRecords)
        {
            if (IsFrameField(record.Key)) { if (insertion < 0) insertion = records.Count; }
            else records.Add(record);
        }
        if (insertion < 0) insertion = records.Count;
        records.InsertRange(insertion, new[] { new KeyValuePair<string, string>("numframes", _frames.Length.ToString(CultureInfo.InvariantCulture)) }
            .Concat(_frames.Select(value => new KeyValuePair<string, string>("frame", value))));
        _newRecords = records.ToArray();
    }

    private static bool IsFrameField(string key) => key.Equals("frame", StringComparison.OrdinalIgnoreCase)
        || key.Equals("numframes", StringComparison.OrdinalIgnoreCase);

    public string Name => "Set thing path frames";

    public void Apply()
    {
        _thing.SourceParameters.Clear();
        _thing.SourceParameters.AddRange(_newRecords);
        _thing.Values.Remove("numframes");
        _thing.Values.Remove("frame");
        _thing.Values["numframes"] = _frames.Length.ToString(CultureInfo.InvariantCulture);
        _thing.Values["frame"] = _frames[^1];
    }

    public void Revert()
    {
        _thing.SourceParameters.Clear();
        _thing.SourceParameters.AddRange(_oldRecords);
        _thing.Values.Clear();
        foreach (var pair in _oldValues) _thing.Values.Add(pair.Key, pair.Value);
    }
}

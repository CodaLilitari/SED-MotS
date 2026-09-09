using Sed.Core.Model;

namespace Sed.Formats.Jkl;

/// <summary>
/// A parsed JKL together with its original source lines and modeled-section
/// baselines. <see cref="JklWriter"/> retains unchanged section text and
/// regenerates changed supported sections; unknown external index references
/// may still require a resource-aware editing workflow.
/// </summary>
public sealed class JklDocument
{
    public string[] SourceLines { get; }
    public Level Level { get; internal set; } = new();

    /// <summary>Global world-vertex index → 0-based source line.</summary>
    public Dictionary<int, int> VertexLine { get; } = new();

    /// <summary>Global world-vertex index → originally-parsed position (to detect edits).</summary>
    public Dictionary<int, Sed.Core.Math.Vec3> VertexOriginal { get; } = new();

    /// <summary>Thing number (model order) → 0-based source line.</summary>
    public Dictionary<int, int> ThingLine { get; } = new();

    /// <summary>0-based source line of the "World things N" count line (-1 if no THINGS section).</summary>
    public int ThingsCountLine { get; set; } = -1;

    /// <summary>Original material names (index order) for resolving surface material indices on save.</summary>
    public List<string> Materials { get; } = new();
    internal Dictionary<string, string> OriginalSections { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> SectionsWithUnmodeledFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PreservationWarnings { get; } = new();

    public JklDocument(string[] sourceLines) => SourceLines = sourceLines;

    /// <summary>
    /// A document built from a model rather than parsed text (imports). Saving
    /// regenerates the GEORESOURCE/SECTORS/THINGS sections from the model and
    /// appends the rest, since there is no source text to preserve.
    /// </summary>
    public JklDocument(Level level) : this(Array.Empty<string>()) => Level = level;
}

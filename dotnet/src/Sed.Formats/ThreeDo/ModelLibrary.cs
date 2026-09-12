using Sed.Formats.Gob;

namespace Sed.Formats.ThreeDo;

/// <summary>
/// Loads and caches 3DO models by name from the resource GOBs. Failures cache as
/// null so a missing/unparseable model is only attempted once.
/// </summary>
public sealed class ModelLibrary
{
    private readonly GobArchive[] _archives;
    private readonly Dictionary<string, ThreeDoModel?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModelLibrary(params GobArchive[] archives) => _archives = archives;

    public ThreeDoModel? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_cache.TryGetValue(name, out var cached)) return cached;

        ThreeDoModel? model = null;
        try
        {
            foreach (var gob in _archives)
            {
                var matches = gob.FindResourceEntries(name, "3do");
                if (matches.Count == 0) continue;
                if (matches.Count != 1)
                    throw new InvalidDataException($"Ambiguous model resource '{name}'.");
                model = ThreeDoParser.Parse(gob.ReadText(matches[0]));
                break;
            }
        }
        catch
        {
            model = null;
        }

        _cache[name] = model;
        return model;
    }
}

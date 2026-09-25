using Sed.Formats.Gob;

namespace Sed.Formats.Cogs;

/// <summary>
/// Resolves a placed COG's script name (e.g. <c>00_door.cog</c>) to its parsed
/// script, using the shared resource lookup policy that prefers <c>cog\</c>.
///
/// Both the level archive and the resource archives must be searched: level-specific
/// scripts live in the episode GOB while shared ones live in the resource GOB. On a
/// retail install all 25 distinct scripts placed in `03katarn` resolve only when
/// both are consulted.
///
/// Lookups are cached, including misses, so a level full of unresolvable scripts
/// does not rescan the archives for every one.
/// </summary>
public sealed class CogScriptLibrary
{
    private readonly List<GobArchive> _archives = new();
    private readonly Dictionary<string, CogScript?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public CogScriptLibrary(params IEnumerable<GobArchive?>[] sources)
    {
        foreach (var group in sources)
            foreach (var archive in group)
                if (archive is not null) _archives.Add(archive);
    }

    /// <summary>The parsed script, or null when missing or ambiguous in the first matching archive.</summary>
    public CogScript? Get(string cogName)
    {
        if (string.IsNullOrWhiteSpace(cogName)) return null;
        if (_cache.TryGetValue(cogName, out var cached)) return cached;

        CogScript? result = null;
        foreach (var archive in _archives)
        {
            var matches = archive.FindResourceEntries(cogName, "cog");
            if (matches.Count == 0) continue;
            // Match the explicit agent provider: a higher-priority ambiguous
            // archive blocks resolution instead of choosing a schema or falling
            // through to another archive. Cache refusal just like a miss.
            if (matches.Count != 1) return _cache[cogName] = null;
            result = CogScript.Parse(cogName, archive.ReadText(matches[0]));
            break;
        }

        _cache[cogName] = result;
        return result;
    }
}

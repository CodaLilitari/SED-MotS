namespace Sed.Formats.Jkl;

internal static class ParameterRecords
{
    public static IEnumerable<KeyValuePair<string, string>> Merge(
        IReadOnlyList<KeyValuePair<string, string>> source, IReadOnlyDictionary<string, string> current)
    {
        var last = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            last[pair.Key] = pair.Value;
            counts[pair.Key] = counts.GetValueOrDefault(pair.Key) + 1;
        }
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            if (!current.TryGetValue(pair.Key, out var value)) continue; // explicit parameter deletion
            if (value == last[pair.Key])
            {
                yield return pair;
                emitted.Add(pair.Key);
            }
            else
            {
                if (counts[pair.Key] > 1)
                    throw new InvalidOperationException($"Cannot replace repeated '{pair.Key}' parameters through a single value; edit the ordered records explicitly.");
                if (emitted.Add(pair.Key)) yield return new(pair.Key, value);
            }
        }
        foreach (var pair in current)
            if (emitted.Add(pair.Key)) yield return pair;
    }
}

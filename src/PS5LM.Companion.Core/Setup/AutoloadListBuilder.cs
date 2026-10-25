namespace PS5LM.Companion.Core.Setup;

/// <summary>Builds Payload Manager's <c>AUTOLOAD_LIST</c>: comma separated slots, with <c>!ms</c> entries as pauses.</summary>
public static class AutoloadListBuilder
{
    /// <summary>
    /// Our slots in order, each followed by its pause, then any entries that were already configured and are not ours.
    /// Earlier pauses are dropped so a repeated setup does not stack them.
    /// </summary>
    public static string Build(IEnumerable<(string Slot, int DelayAfterMs)> ordered, string? existing)
    {
        var entries = new List<string>();
        var ours = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (slot, delay) in ordered)
        {
            entries.Add(slot);
            ours.Add(slot);
            if (delay > 0)
            {
                entries.Add("!" + delay);
            }
        }

        foreach (var entry in (existing ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!entry.StartsWith('!') && ours.Add(entry))
            {
                entries.Add(entry);
            }
        }

        return string.Join(',', entries);
    }
}

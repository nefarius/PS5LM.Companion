namespace PS5LM.Companion.Core.Setup;

/// <summary>Builds Payload Manager's <c>AUTOLOAD_LIST</c>: comma separated slots, with <c>!ms</c> entries as pauses.</summary>
public static class AutoloadListBuilder
{
    /// <summary>
    /// Our slots in order, each followed by its pause, then any entries that were already configured and are not ours.
    /// Another slot of a payload we selected (<c>repo/ftpsrv@…</c> beside <c>local/ftpsrv</c>) is ours too, so boot
    /// does not launch that payload twice. Earlier pauses are dropped so a repeated setup does not stack them.
    /// </summary>
    public static string Build(IEnumerable<(string Slot, int DelayAfterMs)> ordered, string? existing)
    {
        var entries = new List<string>();
        var ours = new HashSet<string>(StringComparer.Ordinal);
        var ourIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, delay) in ordered)
        {
            entries.Add(slot);
            ours.Add(slot);
            if (PayloadId(slot) is { } id)
            {
                ourIds.Add(id);
            }

            if (delay > 0)
            {
                entries.Add("!" + delay);
            }
        }

        foreach (var entry in (existing ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.StartsWith('!') || !ours.Add(entry))
            {
                continue;
            }

            // local/{id} and repo/{id}@{rev} are the same payload. A path or a custom name stays.
            if (PayloadId(entry) is { } id && ourIds.Contains(id))
            {
                continue;
            }

            entries.Add(entry);
        }

        return string.Join(',', entries);
    }

    /// <summary><c>local/{id}</c> or <c>repo/{id}@{rev}</c>; null when the entry does not name a payload that way.</summary>
    private static string? PayloadId(string slot)
    {
        var id = slot.StartsWith("local/", StringComparison.Ordinal) ? slot["local/".Length..]
            : slot.StartsWith("repo/", StringComparison.Ordinal) ? slot["repo/".Length..]
            : null;
        if (id is null)
        {
            return null;
        }

        var at = id.IndexOf('@');
        if (at >= 0)
        {
            id = id[..at];
        }

        return id.Length > 0 && !id.Contains('/') ? id : null;
    }
}

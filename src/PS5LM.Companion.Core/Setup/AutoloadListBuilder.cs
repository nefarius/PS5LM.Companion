namespace PS5LM.Companion.Core.Setup;

/// <summary>
/// Builds Payload Manager's <c>AUTOLOAD_LIST</c>: comma separated slots, with a <c>!ms</c> pause of at least
/// <see cref="StepGapMs"/> between every pair of consecutive entries.
/// </summary>
public static class AutoloadListBuilder
{
    /// <summary>Minimum pause, in milliseconds, between two consecutive autoload entries.</summary>
    public const int StepGapMs = 3000;

    /// <summary>
    /// Our slots in order, then any entries that were already configured and are not ours, with one pause between
    /// each pair. After one of our payloads the pause is the larger of its delay and <see cref="StepGapMs"/>; after a
    /// kept entry it is <see cref="StepGapMs"/>. The last entry has no trailing pause. Another slot of a payload we
    /// selected (<c>repo/ftpsrv@…</c> beside <c>local/ftpsrv</c>) is ours too, so boot does not launch that payload
    /// twice. Earlier pauses are dropped so a repeated setup does not stack them.
    /// </summary>
    public static string Build(IEnumerable<(string Slot, int DelayAfterMs)> ordered, string? existing)
    {
        var slots = new List<(string Slot, int DelayAfterMs)>();
        var ours = new HashSet<string>(StringComparer.Ordinal);
        var ourIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, delay) in ordered)
        {
            slots.Add((slot, delay));
            ours.Add(slot);
            if (PayloadId(slot) is { } id)
            {
                ourIds.Add(id);
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

            slots.Add((entry, 0));
        }

        var entries = new List<string>(slots.Count * 2);
        for (var i = 0; i < slots.Count; i++)
        {
            entries.Add(slots[i].Slot);
            if (i == slots.Count - 1)
            {
                continue;
            }

            entries.Add("!" + Math.Max(slots[i].DelayAfterMs, StepGapMs));
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

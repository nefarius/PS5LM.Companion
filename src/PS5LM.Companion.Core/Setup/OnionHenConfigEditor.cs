namespace PS5LM.Companion.Core.Setup;

/// <summary>What <see cref="OnionHenConfigEditor.AllowTitle"/> did.</summary>
public enum AllowListStatus
{
    /// <summary>The title id was appended.</summary>
    Added,

    /// <summary>The title id was already listed; nothing changed.</summary>
    AlreadyPresent,

    /// <summary>There is no <c>[app_jailbreak]</c> section; nothing changed.</summary>
    SectionMissing,

    /// <summary>The section has no <c>exact_title_ids</c> key; nothing changed.</summary>
    KeyMissing,

    /// <summary>The value is in a form this editor does not understand (for example a bracketed list); nothing changed.</summary>
    UnsupportedFormat,
}

public sealed record AllowListEdit(AllowListStatus Status, string Text);

/// <summary>
/// Adds a title id to <c>exact_title_ids</c> in the <c>[app_jailbreak]</c> section of OnionHEN's <c>config.ini</c>.
/// A pure text function: it keeps every other line, the line endings and the value's separator style.
/// </summary>
public static class OnionHenConfigEditor
{
    public const string Section = "app_jailbreak";
    public const string Key = "exact_title_ids";

    public static AllowListEdit AllowTitle(string config, string titleId)
    {
        var lines = config.Split('\n');
        var inSection = false;
        var sawSection = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = trimmed[1..^1].Trim().Equals(Section, StringComparison.OrdinalIgnoreCase);
                sawSection |= inSection;
                continue;
            }

            if (!inSection || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0 || !line[..equals].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(equals + 1)..];
            var comment = value.IndexOfAny([';', '#']);
            var tail = comment >= 0 ? value[comment..] : string.Empty;
            var body = (comment >= 0 ? value[..comment] : value).Trim();
            if (body.Contains('[', StringComparison.Ordinal) || body.Contains('"', StringComparison.Ordinal))
            {
                return new AllowListEdit(AllowListStatus.UnsupportedFormat, config);
            }

            var separator = body.Contains(';', StringComparison.Ordinal) ? ";" : ",";
            var parts = body.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Any(part => part.Equals(titleId, StringComparison.OrdinalIgnoreCase)))
            {
                return new AllowListEdit(AllowListStatus.AlreadyPresent, config);
            }

            var spaced = body.Contains(separator + " ", StringComparison.Ordinal) ? " " : string.Empty;
            var updated = parts.Length == 0 ? titleId : body + separator + spaced + titleId;
            var prefix = line[..(equals + 1)] + (value.StartsWith(' ') ? " " : string.Empty);
            var rebuilt = prefix + updated + (tail.Length > 0 ? " " + tail : string.Empty);
            lines[i] = rebuilt + (raw.EndsWith('\r') ? "\r" : string.Empty);
            return new AllowListEdit(AllowListStatus.Added, string.Join('\n', lines));
        }

        return new AllowListEdit(sawSection ? AllowListStatus.KeyMissing : AllowListStatus.SectionMissing, config);
    }
}

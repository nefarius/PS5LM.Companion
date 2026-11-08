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

/// <summary>What <see cref="OnionHenConfigEditor.SetValue"/> did.</summary>
public enum ConfigValueStatus
{
    /// <summary>The key already had this value; nothing changed.</summary>
    Unchanged,

    /// <summary>An existing key was given the new value.</summary>
    Updated,

    /// <summary>The key was added, to a section that was already there or to a section appended for it.</summary>
    Added,
}

public sealed record ConfigValueEdit(ConfigValueStatus Status, string Text);

/// <summary>
/// Adds a title id to <c>exact_title_ids</c> in the <c>[app_jailbreak]</c> section of OnionHEN's <c>config.ini</c>.
/// A pure text function: it keeps every other line, the line endings and the value's separator style.
/// </summary>
public static class OnionHenConfigEditor
{
    public const string Section = "app_jailbreak";
    public const string Key = "exact_title_ids";
    public const string KstuffSection = "kstuff";
    public const string KstuffAutoloadKey = "autoload";

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

    /// <summary>
    /// Sets one key in an INI section, keeping every other line, the line endings and a trailing comment.
    /// A missing key is added to its section. A missing section is appended.
    /// </summary>
    public static ConfigValueEdit SetValue(string config, string section, string key, string value)
    {
        var crlf = config.Contains("\r\n", StringComparison.Ordinal);
        var lines = config.Split('\n').ToList();
        var inSection = false;
        var sectionLine = -1;
        var insertAt = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i];
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inSection)
                {
                    insertAt = i;
                    inSection = false;
                }

                if (trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    sectionLine = i;
                    insertAt = lines.Count;
                }

                continue;
            }

            if (!inSection || trimmed.StartsWith('#') || trimmed.StartsWith(';') || !TryKey(line, key, out var equals, out var body, out var tail))
            {
                continue;
            }

            if (body.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return new ConfigValueEdit(ConfigValueStatus.Unchanged, config);
            }

            var prefix = line[..(equals + 1)] + (line[(equals + 1)..].StartsWith(' ') ? " " : string.Empty);
            var rebuilt = prefix + value + (tail.Length > 0 ? " " + tail : string.Empty);
            lines[i] = rebuilt + (raw.EndsWith('\r') ? "\r" : string.Empty);
            return new ConfigValueEdit(ConfigValueStatus.Updated, string.Join('\n', lines));
        }

        var ending = crlf ? "\r" : string.Empty;
        var entry = key + "=" + value + ending;
        if (sectionLine < 0)
        {
            var newline = crlf ? "\r\n" : "\n";
            var gap = config.Length > 0 && !config.EndsWith('\n') ? newline : string.Empty;
            return new ConfigValueEdit(ConfigValueStatus.Added, config + gap + "[" + section + "]" + newline + key + "=" + value + newline);
        }

        if (insertAt == lines.Count && lines.Count > 0 && lines[^1].TrimEnd('\r').Length == 0)
        {
            insertAt--;
        }

        lines.Insert(insertAt, entry);
        return new ConfigValueEdit(ConfigValueStatus.Added, string.Join('\n', lines));
    }

    /// <summary>The trimmed value of <paramref name="key"/> in <paramref name="section"/>, without a trailing comment.</summary>
    public static bool TryGetValue(string config, string section, string key, out string value)
    {
        value = string.Empty;
        var inSection = false;
        foreach (var raw in config.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection || !TryKey(line, key, out _, out var body, out _))
            {
                continue;
            }

            value = body;
            return true;
        }

        return false;
    }

    /// <summary>The value body and any <c>;</c> or <c>#</c> tail of a <paramref name="key"/> line.</summary>
    private static bool TryKey(string line, string key, out int equals, out string body, out string tail)
    {
        equals = line.IndexOf('=', StringComparison.Ordinal);
        body = string.Empty;
        tail = string.Empty;
        if (equals < 0 || !line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = line[(equals + 1)..];
        var comment = value.IndexOfAny([';', '#']);
        tail = comment >= 0 ? value[comment..] : string.Empty;
        body = (comment >= 0 ? value[..comment] : value).Trim();
        return true;
    }
}

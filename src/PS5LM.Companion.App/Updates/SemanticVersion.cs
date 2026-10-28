using System.Globalization;

namespace PS5LM.Companion.App.Updates;

/// <summary>
/// Compares Semantic Version numbers, including prerelease precedence. Build metadata is ignored.
/// </summary>
internal readonly record struct SemanticVersion
{
    private readonly string[] _prerelease;

    private SemanticVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    /// <summary>Parses a version, accepting one leading <c>v</c> and ignoring build metadata.</summary>
    public static bool TryParse(string? raw, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var text = raw.Trim();
        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        var metadata = text.IndexOf('+');
        if (metadata >= 0)
        {
            text = text[..metadata];
        }

        if (text.Length == 0)
        {
            return false;
        }

        var dash = text.IndexOf('-');
        var core = dash < 0 ? text : text[..dash];
        var prerelease = Array.Empty<string>();
        if (dash >= 0)
        {
            var suffix = text[(dash + 1)..];
            if (suffix.Length == 0)
            {
                return false;
            }

            prerelease = suffix.Split('.');
            foreach (var identifier in prerelease)
            {
                if (!IsValidIdentifier(identifier))
                {
                    return false;
                }
            }
        }

        var parts = core.Split('.');
        if (parts.Length != 3
            || !TryParseCore(parts[0], out var major)
            || !TryParseCore(parts[1], out var minor)
            || !TryParseCore(parts[2], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    /// <summary>Returns whether <paramref name="candidate"/> has higher precedence than <paramref name="baseline"/>.</summary>
    public static bool IsNewer(string? candidate, string? baseline)
    {
        if (!TryParse(candidate, out var latest) || !TryParse(baseline, out var local))
        {
            return false;
        }

        return latest.CompareTo(local) > 0;
    }

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        if (minor != 0)
        {
            return minor;
        }

        var patch = Patch.CompareTo(other.Patch);
        if (patch != 0)
        {
            return patch;
        }

        var left = _prerelease ?? [];
        var right = other._prerelease ?? [];
        if (left.Length == 0)
        {
            return right.Length == 0 ? 0 : 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var shared = Math.Min(left.Length, right.Length);
        for (var index = 0; index < shared; index++)
        {
            var compared = CompareIdentifier(left[index], right[index]);
            if (compared != 0)
            {
                return compared;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            return long.Parse(left, CultureInfo.InvariantCulture).CompareTo(long.Parse(right, CultureInfo.InvariantCulture));
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }

    private static bool IsValidIdentifier(string identifier)
    {
        if (identifier.Length == 0)
        {
            return false;
        }

        var numeric = true;
        foreach (var character in identifier)
        {
            if (character is >= '0' and <= '9')
            {
                continue;
            }

            numeric = false;
            if (character is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-'))
            {
                return false;
            }
        }

        if (!numeric)
        {
            return true;
        }

        return identifier.Length == 1 || identifier[0] != '0'
            ? long.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            : false;
    }

    private static bool TryParseCore(string text, out int number)
    {
        number = 0;
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0'))
        {
            return false;
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
    }

    private static bool IsNumeric(string identifier)
    {
        foreach (var character in identifier)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return identifier.Length > 0;
    }
}

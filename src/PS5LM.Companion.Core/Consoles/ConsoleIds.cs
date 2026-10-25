using System.Text.RegularExpressions;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>Console ids appear in URLs, file paths and log scopes, so they are restricted to a safe slug.</summary>
public static partial class ConsoleIds
{
    public static bool IsValid(string? id) => !string.IsNullOrEmpty(id) && Slug().IsMatch(id);

    /// <summary>A slug suggested from a display name. Empty when the name has no letters or digits.</summary>
    public static string Suggest(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(name.Length);
        var pendingHyphen = false;
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                var separator = pendingHyphen && builder.Length > 0;
                if (builder.Length + (separator ? 2 : 1) > 40)
                {
                    break;
                }

                if (separator)
                {
                    builder.Append('-');
                }

                pendingHyphen = false;
                builder.Append(character);
            }
            else
            {
                pendingHyphen = true;
            }

            if (builder.Length >= 40)
            {
                break;
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex Slug();
}

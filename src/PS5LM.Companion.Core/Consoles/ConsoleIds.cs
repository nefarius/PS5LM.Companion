using System.Text.RegularExpressions;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>Console ids appear in URLs, file paths and log scopes, so they are restricted to a safe slug.</summary>
public static partial class ConsoleIds
{
    public static bool IsValid(string? id) => !string.IsNullOrEmpty(id) && Slug().IsMatch(id);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$")]
    private static partial Regex Slug();
}

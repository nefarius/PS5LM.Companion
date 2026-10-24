using System.Text.RegularExpressions;

namespace PS5LM.Companion.Core.Models;

/// <summary>
/// Server-side validation of everything that ends up in a console path or a command file.
/// The companion never forwards a client-supplied path unchecked.
/// </summary>
public static partial class ModelNames
{
    public const int MaxFileNameLength = 200;

    /// <summary>A plain <c>*.gguf</c> file name: no directories, no traversal, no control characters.</summary>
    public static bool IsValidFileName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxFileNameLength
        && name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        && FileNamePattern().IsMatch(name)
        && !name.StartsWith('.')
        && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>A Hugging Face repository id: <c>owner/name</c>.</summary>
    public static bool IsValidRepoId(string? repo) => !string.IsNullOrEmpty(repo) && repo.Length <= 200 && RepoPattern().IsMatch(repo);

    /// <summary>Whether the model id llama-server reports refers to this file.</summary>
    public static bool IsSameModel(string? loadedId, string fileName, string appPath)
    {
        if (string.IsNullOrEmpty(loadedId))
        {
            return false;
        }

        var normalized = loadedId.Replace('\\', '/');

        // A reported path must match exactly: the same file name in another folder is another model.
        // Only a bare name (no directory) is compared with the file name.
        return normalized.Equals(appPath, StringComparison.OrdinalIgnoreCase)
               || (!normalized.Contains('/', StringComparison.Ordinal) && normalized.Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether the model id llama-server reports is the one an operator configured. A configured path must match
    /// exactly; a bare file name matches that file in any folder, because replicas mount their drives differently.
    /// </summary>
    public static bool MatchesConfigured(string? loadedId, string configured)
    {
        if (string.IsNullOrEmpty(loadedId) || string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        var loaded = loadedId.Replace('\\', '/');
        var wanted = configured.Trim().Replace('\\', '/');
        if (wanted.Contains('/', StringComparison.Ordinal))
        {
            return loaded.Equals(wanted, StringComparison.OrdinalIgnoreCase);
        }

        var slash = loaded.LastIndexOf('/');
        return loaded[(slash + 1)..].Equals(wanted, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-+()\[\] ]*$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*/[A-Za-z0-9][A-Za-z0-9._\-]*$")]
    private static partial Regex RepoPattern();
}

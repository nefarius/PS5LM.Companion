using System.Text.RegularExpressions;

namespace PS5LM.Companion.Core.Inference;

public enum InferencePoolOrigin
{
    Configuration = 0,
    User = 1,
}

public enum PoolEditStatus
{
    Ok = 0,
    Invalid = 1,
    Conflict = 2,
    ReadOnly = 3,
    NotFound = 4,
}

public sealed record InferencePool(string Alias, string Model, IReadOnlyList<string> Consoles, InferencePoolOrigin Origin, IReadOnlyList<string>? AlternateNames = null)
{
    public IReadOnlyList<string> Names => AlternateNames ?? [];
}

public sealed record PoolEditResult(PoolEditStatus Status, string? Message, InferencePool? Pool = null)
{
    public bool Succeeded => Status == PoolEditStatus.Ok;
}

public sealed record StoredInferencePool(string Alias, string Model, IReadOnlyList<string> Consoles, IReadOnlyList<string>? AlternateNames = null);

/// <summary>User-added pools. Configuration pools are not stored here.</summary>
public interface IInferencePoolStore
{
    IReadOnlyList<StoredInferencePool> Load();

    void Save(IReadOnlyList<StoredInferencePool> pools);
}

/// <summary>The pools the gateway serves. The list can change after startup.</summary>
public interface IInferencePoolSource
{
    IReadOnlyList<InferencePool> All { get; }

    event Action? Changed;

    InferencePool? Find(string? alias);

    /// <summary>The pool that serves a client's <c>model</c>: by alias, alternate name or alternate prefix.</summary>
    InferencePool? Resolve(string? model);

    PoolEditResult Add(string alias, string model, IReadOnlyList<string> consoles, IReadOnlyList<string>? alternateNames = null);

    /// <summary>Replaces a user pool. A <paramref name="newAlias"/> different from <paramref name="alias"/> renames it. Null <paramref name="alternateNames"/> keeps the current ones.</summary>
    PoolEditResult Update(string alias, string model, IReadOnlyList<string> consoles, string? newAlias = null, IReadOnlyList<string>? alternateNames = null);

    PoolEditResult Remove(string alias);
}

public static partial class InferencePoolRules
{
    public static IReadOnlyList<string> Errors(string alias, string? model, IReadOnlyList<string>? consoles, Func<string, bool> consoleExists, IReadOnlyList<string>? alternateNames = null)
    {
        var failures = new List<string>();
        if (!Alias().IsMatch(alias))
        {
            failures.Add("The alias must be 1-100 letters, digits, '.', '_' or '-', starting with a letter or digit.");
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            failures.Add("A model file name or path is required.");
        }

        if (consoles is null || consoles.Count == 0)
        {
            failures.Add("List at least one console.");
        }
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var console in consoles)
            {
                if (string.IsNullOrWhiteSpace(console))
                {
                    failures.Add("A console id is missing.");
                    continue;
                }

                var id = console.Trim().ToLowerInvariant();
                if (!consoleExists(id))
                {
                    failures.Add($"The console '{id}' is not configured.");
                }
                else if (!seen.Add(id))
                {
                    failures.Add($"The console '{id}' is listed twice.");
                }
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in alternateNames ?? [])
        {
            var name = raw?.Trim() ?? "";
            if (!AlternateName().IsMatch(name))
            {
                failures.Add($"The alternate name '{name}' must be 1-100 letters, digits, '.', '_', '-', ':' or '/', with an optional trailing '*'.");
            }
            else if (name.Equals(alias, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"The alternate name '{name}' is the pool's own alias.");
            }
            else if (!names.Add(name))
            {
                failures.Add($"The alternate name '{name}' is listed twice.");
            }
        }

        return failures;
    }

    public static List<string> NormalizeNames(IReadOnlyList<string>? names) =>
        (names ?? []).Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$")]
    private static partial Regex Alias();

    [GeneratedRegex(@"^(\*|[A-Za-z0-9][A-Za-z0-9._:/\-]{0,98}\*?)$")]
    private static partial Regex AlternateName();
}

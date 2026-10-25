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

public sealed record InferencePool(string Alias, string Model, IReadOnlyList<string> Consoles, InferencePoolOrigin Origin);

public sealed record PoolEditResult(PoolEditStatus Status, string? Message, InferencePool? Pool = null)
{
    public bool Succeeded => Status == PoolEditStatus.Ok;
}

public sealed record StoredInferencePool(string Alias, string Model, IReadOnlyList<string> Consoles);

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

    PoolEditResult Add(string alias, string model, IReadOnlyList<string> consoles);

    PoolEditResult Update(string alias, string model, IReadOnlyList<string> consoles);

    PoolEditResult Remove(string alias);
}

public static partial class InferencePoolRules
{
    public static IReadOnlyList<string> Errors(string alias, string? model, IReadOnlyList<string>? consoles, Func<string, bool> consoleExists)
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

        return failures;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$")]
    private static partial Regex Alias();
}

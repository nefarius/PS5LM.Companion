namespace PS5LM.Companion.Core.Inference;

/// <summary>
/// Maps the <c>model</c> a client sends to a pool alias: first an alias, then an exact alternate name,
/// then the longest alternate prefix (an entry ending in <c>*</c>). Matching ignores case.
/// </summary>
public sealed class InferencePoolNames
{
    private readonly HashSet<string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefix, string Alias)> _prefixes = [];

    public InferencePoolNames(IEnumerable<(string Alias, IReadOnlyList<string> AlternateNames)> pools)
    {
        var list = pools.ToList();
        foreach (var (alias, _) in list)
        {
            _aliases.Add(alias);
        }

        var seenPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, names) in list)
        {
            foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                if (name.EndsWith('*'))
                {
                    var prefix = name[..^1];
                    if (seenPrefixes.Add(prefix))
                    {
                        _prefixes.Add((prefix, alias));
                    }
                }
                else if (!_aliases.Contains(name))
                {
                    _exact.TryAdd(name, alias);
                }
            }
        }

        _prefixes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
    }

    /// <summary>The alias of the pool that serves <paramref name="model"/>, or null.</summary>
    public string? Resolve(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var name = model.Trim();
        if (_aliases.TryGetValue(name, out var alias))
        {
            return alias;
        }

        if (_exact.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (prefix, owner) in _prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return owner;
            }
        }

        return null;
    }

    /// <summary>Names that two pools both claim, so a request could not tell where to go.</summary>
    public static IReadOnlyList<string> Conflicts(IEnumerable<(string Alias, IReadOnlyList<string> AlternateNames)> pools)
    {
        var list = pools.ToList();
        var failures = new List<string>();
        var aliases = new HashSet<string>(list.Select(p => p.Alias), StringComparer.OrdinalIgnoreCase);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, names) in list)
        {
            foreach (var name in names.Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                if (!name.EndsWith('*') && aliases.Contains(name) && !name.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"The name '{name}' of pool '{alias}' is the alias of another pool.");
                }
                else if (claimed.TryGetValue(name, out var owner) && !owner.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add($"The name '{name}' is used by both '{owner}' and '{alias}'.");
                }
                else
                {
                    claimed[name] = alias;
                }
            }
        }

        return failures;
    }
}

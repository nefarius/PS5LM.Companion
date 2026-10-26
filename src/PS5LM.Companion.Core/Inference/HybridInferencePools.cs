using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.Core.Inference;

/// <summary>
/// Configuration pools stay as they were at startup. User pools are stored separately and can change.
/// A stored pool whose alias is also in configuration is ignored.
/// </summary>
public sealed class HybridInferencePools : IInferencePoolSource
{
    private readonly IConsoleRegistry _consoles;
    private readonly IInferencePoolStore? _store;
    private readonly Action<string>? _warn;
    private readonly List<StoredInferencePool> _hidden = [];
    private readonly Lock _gate = new();
    private State _state;

    public HybridInferencePools(InferenceOptions options, IConsoleRegistry consoles, IInferencePoolStore? store = null, Action<string>? warn = null)
    {
        _consoles = consoles;
        _store = store;
        _warn = warn;
        var pools = options.Pools
            .Select(pair => new InferencePool(pair.Key.Trim().ToLowerInvariant(), pair.Value.Model, Normalize(pair.Value.Consoles), InferencePoolOrigin.Configuration, InferencePoolRules.NormalizeNames(pair.Value.AlternateNames)))
            .ToList();
        if (store is not null)
        {
            foreach (var stored in store.Load())
            {
                if (pools.Any(pool => string.Equals(pool.Alias, stored.Alias, StringComparison.OrdinalIgnoreCase)))
                {
                    warn?.Invoke($"Stored pool '{stored.Alias}' is ignored because configuration already defines that alias.");
                    _hidden.Add(stored);
                    continue;
                }

                var candidate = new InferencePool(stored.Alias, stored.Model, stored.Consoles, InferencePoolOrigin.User, InferencePoolRules.NormalizeNames(stored.AlternateNames));
                var clashes = InferencePoolNames.Conflicts(pools.Append(candidate).Select(p => (p.Alias, p.Names)));
                if (clashes.Count > 0)
                {
                    warn?.Invoke($"Stored pool '{stored.Alias}' is ignored: {string.Join(" ", clashes)}");
                    _hidden.Add(stored);
                    continue;
                }

                pools.Add(candidate);
            }
        }

        _state = State.From(pools);
    }

    public IReadOnlyList<InferencePool> All
    {
        get
        {
            lock (_gate)
            {
                return _state.All;
            }
        }
    }

    public event Action? Changed;

    public InferencePool? Find(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return null;
        }

        lock (_gate)
        {
            return _state.ByAlias.TryGetValue(alias.Trim(), out var pool) ? pool : null;
        }
    }

    public InferencePool? Resolve(string? model)
    {
        lock (_gate)
        {
            return _state.Names.Resolve(model) is { } alias && _state.ByAlias.TryGetValue(alias, out var pool) ? pool : null;
        }
    }

    public PoolEditResult Add(string alias, string model, IReadOnlyList<string> consoles, IReadOnlyList<string>? alternateNames = null) =>
        Edit(alias, model, consoles, replacing: false, newAlias: null, alternateNames);

    public PoolEditResult Update(string alias, string model, IReadOnlyList<string> consoles, string? newAlias = null, IReadOnlyList<string>? alternateNames = null) =>
        Edit(alias, model, consoles, replacing: true, newAlias, alternateNames);

    public PoolEditResult Remove(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return new PoolEditResult(PoolEditStatus.NotFound, "The pool is not configured.");
        }

        var key = alias.Trim().ToLowerInvariant();
        InferencePool? removed;
        lock (_gate)
        {
            if (!_state.ByAlias.TryGetValue(key, out var current))
            {
                return new PoolEditResult(PoolEditStatus.NotFound, $"The pool '{key}' is not configured.");
            }

            if (current.Origin == InferencePoolOrigin.Configuration)
            {
                return new PoolEditResult(PoolEditStatus.ReadOnly, $"'{current.Alias}' comes from configuration and cannot be removed.");
            }

            var backup = _state;
            removed = current;
            try
            {
                _state = State.From(_state.All.Where(pool => !string.Equals(pool.Alias, key, StringComparison.OrdinalIgnoreCase)));
                Persist();
            }
            catch
            {
                _state = backup;
                throw;
            }
        }

        Changed?.Invoke();
        return new PoolEditResult(PoolEditStatus.Ok, null, removed);
    }

    private PoolEditResult Edit(string alias, string model, IReadOnlyList<string> consoles, bool replacing, string? newAlias, IReadOnlyList<string>? alternateNames)
    {
        var key = alias?.Trim().ToLowerInvariant() ?? "";
        var target = newAlias is null ? key : newAlias.Trim().ToLowerInvariant();
        var errors = InferencePoolRules.Errors(target, model, consoles, id => _consoles.Find(id) is not null, alternateNames);
        if (errors.Count > 0)
        {
            return new PoolEditResult(PoolEditStatus.Invalid, string.Join(" ", errors));
        }

        lock (_gate)
        {
            _state.ByAlias.TryGetValue(key, out var previous);
            var names = alternateNames is null && previous is not null ? previous.Names.ToList() : InferencePoolRules.NormalizeNames(alternateNames);
            var pool = new InferencePool(target, model.Trim(), Normalize(consoles), InferencePoolOrigin.User, names);
            if (!replacing && previous is not null)
            {
                return new PoolEditResult(PoolEditStatus.Conflict, $"A pool with alias '{key}' already exists.");
            }

            if (replacing && previous is null)
            {
                return new PoolEditResult(PoolEditStatus.NotFound, $"The pool '{key}' is not configured.");
            }

            if (previous?.Origin == InferencePoolOrigin.Configuration)
            {
                return new PoolEditResult(PoolEditStatus.ReadOnly, $"'{previous.Alias}' comes from configuration and cannot be changed.");
            }

            if (!string.Equals(target, key, StringComparison.Ordinal) && _state.ByAlias.ContainsKey(target))
            {
                return new PoolEditResult(PoolEditStatus.Conflict, $"A pool with alias '{target}' already exists.");
            }

            var kept = _state.All.Where(item => !string.Equals(item.Alias, key, StringComparison.OrdinalIgnoreCase)).ToList();
            var clashes = InferencePoolNames.Conflicts(kept.Append(pool).Select(p => (p.Alias, p.Names)));
            if (clashes.Count > 0)
            {
                return new PoolEditResult(PoolEditStatus.Conflict, string.Join(" ", clashes));
            }

            var backup = _state;
            try
            {
                _state = State.From(kept.Append(pool));
                Persist();
            }
            catch
            {
                _state = backup;
                throw;
            }
        }

        Changed?.Invoke();
        return new PoolEditResult(PoolEditStatus.Ok, null, Find(target));
    }

    private static List<string> Normalize(IReadOnlyList<string> consoles) =>
        consoles.Select(console => console.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();

    private void Persist()
    {
        if (_store is null)
        {
            return;
        }

        var pools = _state.All
            .Where(pool => pool.Origin == InferencePoolOrigin.User)
            .Select(pool => new StoredInferencePool(pool.Alias, pool.Model, pool.Consoles, pool.Names))
            .ToList();
        foreach (var hidden in _hidden)
        {
            if (pools.Any(pool => string.Equals(pool.Alias, hidden.Alias, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            pools.Add(hidden);
        }

        _store.Save(pools);
    }

    private sealed class State
    {
        public required IReadOnlyList<InferencePool> All { get; init; }

        public required Dictionary<string, InferencePool> ByAlias { get; init; }

        public required InferencePoolNames Names { get; init; }

        public static State From(IEnumerable<InferencePool> pools)
        {
            var ordered = pools.OrderBy(pool => pool.Alias, StringComparer.OrdinalIgnoreCase).ToList();
            return new State
            {
                All = ordered,
                ByAlias = ordered.ToDictionary(pool => pool.Alias, StringComparer.OrdinalIgnoreCase),
                Names = new InferencePoolNames(ordered.Select(pool => (pool.Alias, pool.Names))),
            };
        }
    }
}

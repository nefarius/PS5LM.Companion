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
    private readonly Lock _gate = new();
    private State _state;

    public HybridInferencePools(InferenceOptions options, IConsoleRegistry consoles, IInferencePoolStore? store = null, Action<string>? warn = null)
    {
        _consoles = consoles;
        _store = store;
        _warn = warn;
        var pools = options.Pools
            .Select(pair => new InferencePool(pair.Key.Trim().ToLowerInvariant(), pair.Value.Model, Normalize(pair.Value.Consoles), InferencePoolOrigin.Configuration))
            .ToList();
        if (store is not null)
        {
            foreach (var stored in store.Load())
            {
                if (pools.Any(pool => string.Equals(pool.Alias, stored.Alias, StringComparison.OrdinalIgnoreCase)))
                {
                    warn?.Invoke($"Stored pool '{stored.Alias}' is ignored because configuration already defines that alias.");
                    continue;
                }

                pools.Add(new InferencePool(stored.Alias, stored.Model, stored.Consoles, InferencePoolOrigin.User));
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

    public PoolEditResult Add(string alias, string model, IReadOnlyList<string> consoles) => Edit(alias, model, consoles, replacing: false);

    public PoolEditResult Update(string alias, string model, IReadOnlyList<string> consoles) => Edit(alias, model, consoles, replacing: true);

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

    private PoolEditResult Edit(string alias, string model, IReadOnlyList<string> consoles, bool replacing)
    {
        var key = alias?.Trim().ToLowerInvariant() ?? "";
        var errors = InferencePoolRules.Errors(key, model, consoles, id => _consoles.Find(id) is not null);
        if (errors.Count > 0)
        {
            return new PoolEditResult(PoolEditStatus.Invalid, string.Join(" ", errors));
        }

        var pool = new InferencePool(key, model.Trim(), Normalize(consoles), InferencePoolOrigin.User);
        lock (_gate)
        {
            _state.ByAlias.TryGetValue(key, out var previous);
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

            var backup = _state;
            try
            {
                var kept = _state.All.Where(item => !string.Equals(item.Alias, key, StringComparison.OrdinalIgnoreCase));
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
        return new PoolEditResult(PoolEditStatus.Ok, null, pool);
    }

    private static List<string> Normalize(IReadOnlyList<string> consoles) =>
        consoles.Select(console => console.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();

    private void Persist() => _store?.Save(_state.All
        .Where(pool => pool.Origin == InferencePoolOrigin.User)
        .Select(pool => new StoredInferencePool(pool.Alias, pool.Model, pool.Consoles))
        .ToList());

    private sealed class State
    {
        public required IReadOnlyList<InferencePool> All { get; init; }

        public required Dictionary<string, InferencePool> ByAlias { get; init; }

        public static State From(IEnumerable<InferencePool> pools)
        {
            var ordered = pools.OrderBy(pool => pool.Alias, StringComparer.OrdinalIgnoreCase).ToList();
            return new State
            {
                All = ordered,
                ByAlias = ordered.ToDictionary(pool => pool.Alias, StringComparer.OrdinalIgnoreCase),
            };
        }
    }
}

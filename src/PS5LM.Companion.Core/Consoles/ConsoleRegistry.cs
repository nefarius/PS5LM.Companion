using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>
/// One runtime per console. Configuration consoles are fixed; user consoles can be added, edited and removed,
/// and are written through <see cref="IConsoleStore"/>. A stored console that repeats a configuration id is ignored.
/// </summary>
public sealed class ConsoleRegistry : IConsoleRegistry, IDisposable
{
    private readonly IConsoleConnectorFactory _factory;
    private readonly TimeProvider _time;
    private readonly IConsoleStore? _store;
    private readonly Action<string>? _warn;
    private readonly Lock _gate = new();
    private State _state;

    public ConsoleRegistry(
        ConsolesOptions options,
        IConsoleConnectorFactory factory,
        TimeProvider time,
        IConsoleStore? store = null,
        Action<string>? warn = null)
    {
        _factory = factory;
        _time = time;
        _store = store;
        _warn = warn;

        var runtimes = new List<ConsoleRuntime>();
        foreach (var (key, consoleOptions) in options.Consoles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            runtimes.Add(Create(key.ToLowerInvariant(), consoleOptions, ConsoleSource.Configuration));
        }

        if (store is not null)
        {
            foreach (var stored in store.Load())
            {
                if (runtimes.Any(runtime => string.Equals(runtime.Id, stored.Id, StringComparison.Ordinal)))
                {
                    warn?.Invoke($"Stored console '{stored.Id}' is ignored because configuration already defines that id.");
                    continue;
                }

                runtimes.Add(Create(stored.Id, stored.Options, ConsoleSource.User));
            }
        }

        _state = State.From(runtimes);
    }

    public IReadOnlyList<ConsoleRuntime> All
    {
        get
        {
            lock (_gate)
            {
                return _state.All;
            }
        }
    }

    public event Action<ConsoleChange>? Changed;

    public ConsoleRuntime? Find(string? id)
    {
        if (id is null)
        {
            return null;
        }

        lock (_gate)
        {
            return _state.ById.TryGetValue(id.ToLowerInvariant(), out var runtime) ? runtime : null;
        }
    }

    public ConsoleEditResult Add(string id, PS5Options options) => Edit(id, options, replacing: false);

    public ConsoleEditResult Update(string id, PS5Options options) => Edit(id, options, replacing: true);

    public ConsoleEditResult Remove(string id)
    {
        var key = id.Trim().ToLowerInvariant();
        ConsoleRuntime? removed = null;
        lock (_gate)
        {
            if (!_state.ById.TryGetValue(key, out var current))
            {
                return new ConsoleEditResult(ConsoleEditStatus.NotFound, $"The console '{key}' is not configured.");
            }

            if (current.Source == ConsoleSource.Configuration)
            {
                return new ConsoleEditResult(ConsoleEditStatus.ReadOnly, $"'{current.Name}' comes from configuration and cannot be removed.");
            }

            var backup = _state;
            removed = current;
            try
            {
                _state = State.From(_state.All.Where(runtime => !string.Equals(runtime.Id, key, StringComparison.Ordinal)));
                Persist();
            }
            catch
            {
                _state = backup;
                removed = null;
                throw;
            }
        }

        if (removed is null)
        {
            return new ConsoleEditResult(ConsoleEditStatus.NotFound, $"The console '{key}' is not configured.");
        }

        Changed?.Invoke(new ConsoleChange(ConsoleChangeKind.Removed, removed, null));
        removed.Dispose();
        return new ConsoleEditResult(ConsoleEditStatus.Ok, null, removed);
    }

    public void Dispose()
    {
        foreach (var runtime in All)
        {
            runtime.Dispose();
        }
    }

    private ConsoleEditResult Edit(string id, PS5Options options, bool replacing)
    {
        var key = id.Trim().ToLowerInvariant();
        var errors = ConsoleSettings.Errors(key, options);
        if (errors.Count > 0)
        {
            return new ConsoleEditResult(ConsoleEditStatus.Invalid, string.Join(" ", errors));
        }

        ConsoleRuntime? previous = null;
        ConsoleRuntime? created = null;
        lock (_gate)
        {
            _state.ById.TryGetValue(key, out previous);
            if (!replacing && previous is not null)
            {
                return new ConsoleEditResult(ConsoleEditStatus.Conflict, $"A console with id '{key}' already exists.");
            }

            if (replacing && previous is null)
            {
                return new ConsoleEditResult(ConsoleEditStatus.NotFound, $"The console '{key}' is not configured.");
            }

            if (previous?.Source == ConsoleSource.Configuration)
            {
                return new ConsoleEditResult(ConsoleEditStatus.ReadOnly, $"'{previous.Name}' comes from configuration and cannot be changed.");
            }

            created = Create(key, options, ConsoleSource.User);
            var backup = _state;
            try
            {
                var kept = _state.All.Where(runtime => !string.Equals(runtime.Id, key, StringComparison.Ordinal));
                _state = State.From(kept.Append(created));
                Persist();
            }
            catch
            {
                _state = backup;
                created.Dispose();
                throw;
            }
        }

        Changed?.Invoke(new ConsoleChange(replacing ? ConsoleChangeKind.Replaced : ConsoleChangeKind.Added, previous, created));
        previous?.Dispose();
        return new ConsoleEditResult(ConsoleEditStatus.Ok, null, created);
    }

    private ConsoleRuntime Create(string id, PS5Options options, ConsoleSource source) =>
        new(id, options, _factory.Create(id, options), _time, source);

    private void Persist() => _store?.Save(_state.All
        .Where(runtime => runtime.Source == ConsoleSource.User)
        .Select(runtime => new StoredConsole(runtime.Id, runtime.Options))
        .ToList());

    private sealed class State
    {
        public required IReadOnlyList<ConsoleRuntime> All { get; init; }

        public required Dictionary<string, ConsoleRuntime> ById { get; init; }

        public static State From(IEnumerable<ConsoleRuntime> runtimes)
        {
            var ordered = runtimes.OrderBy(runtime => runtime.Id, StringComparer.Ordinal).ToList();
            return new State
            {
                All = ordered,
                ById = ordered.ToDictionary(runtime => runtime.Id, StringComparer.Ordinal),
            };
        }
    }
}

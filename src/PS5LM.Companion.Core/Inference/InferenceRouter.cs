using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.Core.Inference;

public enum AdmissionFailure
{
    None = 0,

    /// <summary>No pool has this alias.</summary>
    UnknownModel,

    /// <summary>No console in the pool is serving the configured model right now.</summary>
    NoReplica,

    /// <summary>Every replica is busy and the wait queue is full.</summary>
    QueueFull,

    /// <summary>Every replica stayed busy until the queue timeout.</summary>
    Timeout,
}

/// <summary>How a request's affinity key shaped the choice of console.</summary>
public enum AffinityOutcome
{
    /// <summary>No key, or affinity is off.</summary>
    None = 0,

    /// <summary>The key was not known: the console was chosen normally and remembered.</summary>
    New,

    /// <summary>The console that served the conversation last was chosen again.</summary>
    Hit,

    /// <summary>The previous console was busy or not serving: another one was chosen and remembered.</summary>
    Moved,
}

/// <summary>The outcome of asking for a replica: a lease on one console, or the reason there is none.</summary>
public sealed record InferenceAdmission(InferenceLease? Lease, AdmissionFailure Failure, string? Message, string? Pool = null, AffinityOutcome Affinity = AffinityOutcome.None)
{
    public static InferenceAdmission Fail(AdmissionFailure failure, string message) => new(null, failure, message);
}

/// <summary>One request's hold on a console. Disposing it frees the slot and admits the next waiting request.</summary>
public sealed class InferenceLease
{
    private readonly InferenceRouter _router;
    private int _released;

    internal InferenceLease(InferenceRouter router, ConsoleRuntime console)
    {
        _router = router;
        Console = console;
    }

    public ConsoleRuntime Console { get; }

    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _router.Release(Console.Id);
        }
    }
}

/// <summary>Why one configured console is or is not serving a pool.</summary>
public sealed record ReplicaSnapshot(string ConsoleId, CompanionLifecycle Lifecycle, string? LoadedModel, int ActiveRequests, bool Eligible, string Reason);

/// <summary>One public model alias with its replicas.</summary>
public sealed record PoolSnapshot(string Alias, string Model, int EligibleReplicas, IReadOnlyList<ReplicaSnapshot> Replicas, InferencePoolOrigin Origin = InferencePoolOrigin.Configuration, IReadOnlyList<string>? AlternateNames = null);

/// <summary>
/// Chooses which console serves a request. A console is a candidate only while it reports the pool's model as
/// loaded and is serving; among candidates with a free slot the least busy wins, and a request waits (bounded)
/// when all are full. The router never retries: a generation that may have started is not repeated elsewhere.
/// </summary>
public sealed class InferenceRouter
{
    private readonly IConsoleRegistry _registry;
    private readonly InferenceOptions _options;
    private readonly IInferencePoolSource? _pools;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastAssigned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastReleased = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Affinity> _affinity = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly LinkedList<Waiter> _waiters = new();
    private long _sequence;

    private const int MaxAffinityEntries = 1024;

    public InferenceRouter(IConsoleRegistry registry, InferenceOptions options, IInferencePoolSource? pools = null, TimeProvider? time = null)
    {
        _registry = registry;
        _options = options;
        _pools = pools;
        _time = time ?? TimeProvider.System;
        if (pools is not null)
        {
            pools.Changed += OnPoolsChanged;
        }
    }

    public bool Enabled => PoolPairs().Any();

    /// <summary>The configured aliases, in alphabetical order.</summary>
    public IReadOnlyList<string> Aliases => PoolPairs().Select(pair => pair.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public bool HasAlias(string alias) =>
        _pools is null ? _options.Pools.ContainsKey(alias) : _pools.Find(alias) is not null;

    /// <summary>The only alias when exactly one pool exists; used when a request names no model.</summary>
    public string? SoleAlias
    {
        get
        {
            var pairs = PoolPairs().ToList();
            return pairs.Count == 1 ? pairs[0].Key : null;
        }
    }

    /// <param name="onlyConsole">Restricts the choice to one console of the pool, for tests that must reach a specific replica.</param>
    /// <param name="alias">A pool alias, or any alternate name or prefix that resolves to one.</param>
    /// <param name="affinityKey">Identifies a conversation; its requests prefer the console that served it last, which still holds its prompt cache.</param>
    public async Task<InferenceAdmission> AcquireAsync(string alias, CancellationToken cancellationToken, string? onlyConsole = null, string? affinityKey = null)
    {
        var requested = alias;
        if (!TryGetPool(alias, out var pool, out var resolved))
        {
            return InferenceAdmission.Fail(AdmissionFailure.UnknownModel, $"The model '{requested}' is not served by this gateway.");
        }

        alias = resolved;

        Waiter waiter;
        LinkedListNode<Waiter> node;
        lock (_gate)
        {
            var eligible = Eligible(pool, onlyConsole);
            if (eligible.Count == 0)
            {
                return InferenceAdmission.Fail(AdmissionFailure.NoReplica, $"No console is serving '{alias}' right now.");
            }

            if (Pick(alias, eligible, affinityKey) is ({ } picked, var outcome))
            {
                return new InferenceAdmission(Take(picked), AdmissionFailure.None, null, alias, outcome);
            }

            if (_waiters.Count >= _options.MaxQueuedRequests)
            {
                return InferenceAdmission.Fail(AdmissionFailure.QueueFull, $"Every console serving '{alias}' is busy and the queue is full.");
            }

            waiter = new Waiter(alias, onlyConsole, affinityKey);
            node = _waiters.AddLast(waiter);
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.QueueTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            while (true)
            {
                try
                {
                    return await waiter.Completion.Task.WaitAsync(TimeSpan.FromSeconds(1), linked.Token);
                }
                catch (TimeoutException)
                {
                    // Replicas can change state without a lease being released: look again.
                    lock (_gate)
                    {
                        Dispatch();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (waiter.Completion.Task.IsCompletedSuccessfully)
                {
                    // Admitted at the same moment the wait ended: hand the slot back, or the caller's cancellation wins.
                    var admission = waiter.Completion.Task.Result;
                    if (admission.Lease is null || !cancellationToken.IsCancellationRequested)
                    {
                        return admission;
                    }

                    admission.Lease.Release();
                }
                else
                {
                    _waiters.Remove(node);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return InferenceAdmission.Fail(AdmissionFailure.Timeout, $"Every console serving '{alias}' stayed busy for {_options.QueueTimeoutSeconds} seconds.");
        }
    }

    public IReadOnlyList<PoolSnapshot> Snapshot()
    {
        lock (_gate)
        {
            return PoolPairs()
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var replicas = p.Value.Consoles
                        .Select(id => _registry.Find(id) is { } console
                            ? Describe(console, p.Value)
                            : new ReplicaSnapshot(id, CompanionLifecycle.Disconnected, null, 0, false, "Console is not configured."))
                        .ToList();
                    return new PoolSnapshot(p.Key, p.Value.Model, replicas.Count(r => r.Eligible), replicas, p.Origin, p.Value.AlternateNames);
                })
                .ToList();
        }
    }

    internal void Release(string consoleId)
    {
        lock (_gate)
        {
            _active[consoleId] = Math.Max(0, _active.GetValueOrDefault(consoleId) - 1);
            _lastReleased[consoleId] = _time.GetUtcNow();
            Dispatch();
        }
    }

    private ReplicaSnapshot Describe(ConsoleRuntime console, InferencePoolOptions pool)
    {
        var status = console.Store.Current;
        var reason = EligibilityReason(status, pool);
        return new ReplicaSnapshot(console.Id, status.Lifecycle, status.LoadedModel, _active.GetValueOrDefault(console.Id), reason is null, reason ?? "Serving the model.");
    }

    private static string? EligibilityReason(CompanionStatus status, InferencePoolOptions pool)
    {
        if (status.Lifecycle is not (CompanionLifecycle.Ready or CompanionLifecycle.Generating))
        {
            return $"Not serving (lifecycle {status.Lifecycle}).";
        }

        return ModelNames.MatchesConfigured(status.LoadedModel, pool.Model)
            ? null
            : $"Loaded model is '{status.LoadedModel ?? "unknown"}', not '{pool.Model}'.";
    }

    private List<ConsoleRuntime> Eligible(InferencePoolOptions pool, string? onlyConsole = null) =>
        pool.Consoles
            .Where(id => onlyConsole is null || id.Equals(onlyConsole, StringComparison.OrdinalIgnoreCase))
            .Select(id => _registry.Find(id))
            .Where(c => c is not null && EligibilityReason(c.Store.Current, pool) is null)
            .Select(c => c!)
            .ToList();

    /// <summary>
    /// The console for a request: the one that served the conversation last when it has a free slot (its prompt cache is
    /// still there), else the least busy eligible console with a free slot. Runs under the lock; remembers the choice.
    /// </summary>
    private (ConsoleRuntime? Console, AffinityOutcome Outcome) Pick(string alias, List<ConsoleRuntime> eligible, string? key)
    {
        var free = eligible.Where(c => _active.GetValueOrDefault(c.Id) < _options.MaxConcurrentPerConsole).ToList();
        if (free.Count == 0)
        {
            return (null, AffinityOutcome.None);
        }

        var tracked = key is not null && _options.AffinityMinutes > 0;
        string? entryKey = null;
        string? previous = null;
        if (tracked)
        {
            entryKey = $"{alias}\n{key}";
            if (_affinity.TryGetValue(entryKey, out var entry))
            {
                if (_time.GetUtcNow() - entry.LastUsed <= TimeSpan.FromMinutes(_options.AffinityMinutes))
                {
                    previous = entry.ConsoleId;
                }
                else
                {
                    _affinity.Remove(entryKey);
                }
            }
        }

        var affine = previous is null ? null : free.FirstOrDefault(c => c.Id.Equals(previous, StringComparison.OrdinalIgnoreCase));
        var picked = affine ?? free
            .OrderBy(c => _active.GetValueOrDefault(c.Id))
            .ThenBy(c => IsGenerating(c) ? 1 : 0)
            .ThenBy(c => _lastAssigned.GetValueOrDefault(c.Id))
            .First();

        if (!tracked)
        {
            return (picked, AffinityOutcome.None);
        }

        Remember(entryKey!, picked.Id);
        return (picked, affine is not null ? AffinityOutcome.Hit : previous is not null ? AffinityOutcome.Moved : AffinityOutcome.New);
    }

    /// <summary>
    /// The status is polled every few seconds, so right after the gateway released a console it can still read
    /// Generating. Only a status taken after the last release shows load from somewhere else.
    /// </summary>
    private bool IsGenerating(ConsoleRuntime console)
    {
        var status = console.Store.Current;
        return status.Lifecycle == CompanionLifecycle.Generating
               && (!_lastReleased.TryGetValue(console.Id, out var released) || status.UpdatedAt > released);
    }

    private void Remember(string entryKey, string consoleId)
    {
        var now = _time.GetUtcNow();
        if (!_affinity.ContainsKey(entryKey) && _affinity.Count >= MaxAffinityEntries)
        {
            var ttl = TimeSpan.FromMinutes(_options.AffinityMinutes);
            foreach (var expired in _affinity.Where(pair => now - pair.Value.LastUsed > ttl).Select(pair => pair.Key).ToList())
            {
                _affinity.Remove(expired);
            }

            if (_affinity.Count >= MaxAffinityEntries)
            {
                _affinity.Remove(_affinity.MinBy(pair => pair.Value.LastUsed).Key);
            }
        }

        _affinity[entryKey] = new Affinity(consoleId, now);
    }

    private readonly record struct Affinity(string ConsoleId, DateTimeOffset LastUsed);

    private InferenceLease Take(ConsoleRuntime console)
    {
        _active[console.Id] = _active.GetValueOrDefault(console.Id) + 1;
        _lastAssigned[console.Id] = ++_sequence;
        return new InferenceLease(this, console);
    }

    /// <summary>Admits waiting requests, oldest first, while a free replica exists. Runs under the lock.</summary>
    private void Dispatch()
    {
        for (var node = _waiters.First; node is not null;)
        {
            var next = node.Next;
            var waiter = node.Value;
            if (!TryGetPool(waiter.Alias, out var pool))
            {
                _waiters.Remove(node);
                waiter.Completion.TrySetResult(InferenceAdmission.Fail(AdmissionFailure.UnknownModel, $"The model '{waiter.Alias}' is not served by this gateway."));
                node = next;
                continue;
            }

            var eligible = Eligible(pool, waiter.OnlyConsole);
            if (eligible.Count == 0)
            {
                _waiters.Remove(node);
                waiter.Completion.TrySetResult(InferenceAdmission.Fail(AdmissionFailure.NoReplica, $"No console is serving '{waiter.Alias}' any more."));
            }
            else if (Pick(waiter.Alias, eligible, waiter.AffinityKey) is ({ } picked, var outcome))
            {
                _waiters.Remove(node);
                waiter.Completion.TrySetResult(new InferenceAdmission(Take(picked), AdmissionFailure.None, null, waiter.Alias, outcome));
            }

            node = next;
        }
    }

    private void OnPoolsChanged()
    {
        lock (_gate)
        {
            Dispatch();
        }
    }

    private bool TryGetPool(string alias, out InferencePoolOptions pool) => TryGetPool(alias, out pool, out _);

    private bool TryGetPool(string alias, out InferencePoolOptions pool, out string resolved)
    {
        resolved = alias;
        if (_pools is null)
        {
            var names = new InferencePoolNames(_options.Pools.Select(p => (p.Key, (IReadOnlyList<string>)p.Value.AlternateNames)));
            if (names.Resolve(alias) is { } key && _options.Pools.TryGetValue(key, out pool!))
            {
                resolved = key;
                return true;
            }

            pool = null!;
            return false;
        }

        var found = _pools.Resolve(alias);
        if (found is null)
        {
            pool = null!;
            return false;
        }

        pool = new InferencePoolOptions { Model = found.Model, Consoles = found.Consoles.ToList(), AlternateNames = found.Names.ToList() };
        resolved = found.Alias;
        return true;
    }

    private IEnumerable<(string Key, InferencePoolOptions Value, InferencePoolOrigin Origin)> PoolPairs()
    {
        if (_pools is null)
        {
            return _options.Pools.Select(pair => (pair.Key, pair.Value, InferencePoolOrigin.Configuration));
        }

        return _pools.All.Select(pool => (
            pool.Alias,
            new InferencePoolOptions { Model = pool.Model, Consoles = pool.Consoles.ToList(), AlternateNames = pool.Names.ToList() },
            pool.Origin));
    }

    private sealed class Waiter(string alias, string? onlyConsole, string? affinityKey)
    {
        public string? AffinityKey { get; } = affinityKey;

        public string? OnlyConsole { get; } = onlyConsole;

        public string Alias { get; } = alias;

        public TaskCompletionSource<InferenceAdmission> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

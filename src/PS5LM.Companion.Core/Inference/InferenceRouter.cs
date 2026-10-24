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

/// <summary>The outcome of asking for a replica: a lease on one console, or the reason there is none.</summary>
public sealed record InferenceAdmission(InferenceLease? Lease, AdmissionFailure Failure, string? Message)
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
public sealed record PoolSnapshot(string Alias, string Model, int EligibleReplicas, IReadOnlyList<ReplicaSnapshot> Replicas);

/// <summary>
/// Chooses which console serves a request. A console is a candidate only while it reports the pool's model as
/// loaded and is serving; among candidates with a free slot the least busy wins, and a request waits (bounded)
/// when all are full. The router never retries: a generation that may have started is not repeated elsewhere.
/// </summary>
public sealed class InferenceRouter
{
    private readonly IConsoleRegistry _registry;
    private readonly InferenceOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastAssigned = new(StringComparer.Ordinal);
    private readonly LinkedList<Waiter> _waiters = new();
    private long _sequence;

    public InferenceRouter(IConsoleRegistry registry, InferenceOptions options)
    {
        _registry = registry;
        _options = options;
    }

    public bool Enabled => _options.Enabled;

    /// <summary>The configured aliases, in alphabetical order.</summary>
    public IReadOnlyList<string> Aliases => _options.Pools.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public bool HasAlias(string alias) => _options.Pools.ContainsKey(alias);

    /// <summary>The only alias when exactly one pool exists; used when a request names no model.</summary>
    public string? SoleAlias => _options.Pools.Count == 1 ? _options.Pools.Keys.Single() : null;

    /// <param name="onlyConsole">Restricts the choice to one console of the pool, for tests that must reach a specific replica.</param>
    public async Task<InferenceAdmission> AcquireAsync(string alias, CancellationToken cancellationToken, string? onlyConsole = null)
    {
        if (!_options.Pools.TryGetValue(alias, out var pool))
        {
            return InferenceAdmission.Fail(AdmissionFailure.UnknownModel, $"The model '{alias}' is not served by this gateway.");
        }

        Waiter waiter;
        LinkedListNode<Waiter> node;
        lock (_gate)
        {
            var eligible = Eligible(pool, onlyConsole);
            if (eligible.Count == 0)
            {
                return InferenceAdmission.Fail(AdmissionFailure.NoReplica, $"No console is serving '{alias}' right now.");
            }

            if (TryPick(eligible) is { } picked)
            {
                return new InferenceAdmission(Take(picked), AdmissionFailure.None, null);
            }

            if (_waiters.Count >= _options.MaxQueuedRequests)
            {
                return InferenceAdmission.Fail(AdmissionFailure.QueueFull, $"Every console serving '{alias}' is busy and the queue is full.");
            }

            waiter = new Waiter(alias, pool, onlyConsole);
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
            return _options.Pools
                .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    var replicas = p.Value.Consoles
                        .Select(id => _registry.Find(id))
                        .Where(c => c is not null)
                        .Select(c => Describe(c!, p.Value))
                        .ToList();
                    return new PoolSnapshot(p.Key, p.Value.Model, replicas.Count(r => r.Eligible), replicas);
                })
                .ToList();
        }
    }

    internal void Release(string consoleId)
    {
        lock (_gate)
        {
            _active[consoleId] = Math.Max(0, _active.GetValueOrDefault(consoleId) - 1);
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

    /// <summary>The least busy eligible console with a free slot: fewest active, idle before generating, least recently used.</summary>
    private ConsoleRuntime? TryPick(List<ConsoleRuntime> eligible) =>
        eligible
            .Where(c => _active.GetValueOrDefault(c.Id) < _options.MaxConcurrentPerConsole)
            .OrderBy(c => _active.GetValueOrDefault(c.Id))
            .ThenBy(c => c.Store.Current.Lifecycle == CompanionLifecycle.Generating ? 1 : 0)
            .ThenBy(c => _lastAssigned.GetValueOrDefault(c.Id))
            .FirstOrDefault();

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
            var eligible = Eligible(waiter.Pool, waiter.OnlyConsole);
            if (eligible.Count == 0)
            {
                _waiters.Remove(node);
                waiter.Completion.TrySetResult(InferenceAdmission.Fail(AdmissionFailure.NoReplica, $"No console is serving '{waiter.Alias}' any more."));
            }
            else if (TryPick(eligible) is { } picked)
            {
                _waiters.Remove(node);
                waiter.Completion.TrySetResult(new InferenceAdmission(Take(picked), AdmissionFailure.None, null));
            }

            node = next;
        }
    }

    private sealed class Waiter(string alias, InferencePoolOptions pool, string? onlyConsole)
    {
        public string? OnlyConsole { get; } = onlyConsole;

        public string Alias { get; } = alias;

        public InferencePoolOptions Pool { get; } = pool;

        public TaskCompletionSource<InferenceAdmission> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

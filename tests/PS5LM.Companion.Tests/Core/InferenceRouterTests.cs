using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class InferenceRouterTests
{
    private const string Model = "qwen.gguf";

    private static (InferenceRouter Router, ConsoleRegistry Registry) Create(
        string[] consoles,
        Action<InferenceOptions>? configure = null,
        string model = Model)
    {
        var consoleOptions = new ConsolesOptions();
        foreach (var id in consoles)
        {
            consoleOptions.Consoles[id] = new PS5Options { Host = "127.0.0.1" };
        }

        var registry = new ConsoleRegistry(consoleOptions, new FakeConnectorFactory(), TimeProvider.System);
        var options = new InferenceOptions { QueueTimeoutSeconds = 5 };
        options.Pools["chat"] = new InferencePoolOptions { Model = model, Consoles = [.. consoles] };
        configure?.Invoke(options);
        return (new InferenceRouter(registry, options), registry);
    }

    private static void Serve(ConsoleRegistry registry, string id, string? loaded = "/data/PS5LM/models/qwen.gguf", CompanionLifecycle lifecycle = CompanionLifecycle.Ready) =>
        registry.Find(id)!.Store.Publish(CompanionStatus.Initial with { Lifecycle = lifecycle, LoadedModel = loaded });

    [Theory]
    [InlineData("/data/PS5LM/models/qwen.gguf", "qwen.gguf", true)]
    [InlineData("/mnt/usb0/PS5LM/models/QWEN.gguf", "qwen.gguf", true)]
    [InlineData("qwen.gguf", "qwen.gguf", true)]
    [InlineData("/data/PS5LM/models/qwen.gguf", "/data/PS5LM/models/qwen.gguf", true)]
    [InlineData("/mnt/usb0/PS5LM/models/qwen.gguf", "/data/PS5LM/models/qwen.gguf", false)]
    [InlineData("/data/PS5LM/models/qwen2.gguf", "qwen.gguf", false)]
    [InlineData("/data/PS5LM/models/other-qwen.gguf", "qwen.gguf", false)]
    [InlineData(null, "qwen.gguf", false)]
    public void Configured_model_matching(string? loaded, string configured, bool expected) =>
        Assert.Equal(expected, ModelNames.MatchesConfigured(loaded, configured));

    [Fact]
    public async Task Only_consoles_serving_the_configured_model_are_chosen()
    {
        var (router, registry) = Create(["a", "b", "c"]);
        Serve(registry, "a", "/data/PS5LM/models/llama.gguf");
        Serve(registry, "b", lifecycle: CompanionLifecycle.Loading);
        Serve(registry, "c");

        var admission = await router.AcquireAsync("chat", CancellationToken.None);

        Assert.Equal("c", admission.Lease!.Console.Id);
        var pool = Assert.Single(router.Snapshot());
        Assert.Equal(1, pool.EligibleReplicas);
        Assert.Contains("llama.gguf", pool.Replicas.Single(r => r.ConsoleId == "a").Reason, StringComparison.Ordinal);
        Assert.Contains("Loading", pool.Replicas.Single(r => r.ConsoleId == "b").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pool_console_that_is_not_configured_is_reported()
    {
        var (router, _) = Create(["a"], options => options.Pools["chat"].Consoles.Add("missing"));

        var replica = Assert.Single(router.Snapshot()).Replicas.Single(item => item.ConsoleId == "missing");

        Assert.False(replica.Eligible);
        Assert.Contains("not configured", replica.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_least_busy_console_wins_and_ties_rotate()
    {
        var (router, registry) = Create(["a", "b"], o => o.MaxConcurrentPerConsole = 2);
        Serve(registry, "a");
        Serve(registry, "b");

        var first = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;
        var second = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;
        first.Release();
        var third = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;

        Assert.NotEqual(first.Console.Id, second.Console.Id);
        Assert.Equal(first.Console.Id, third.Console.Id);
    }

    [Fact]
    public async Task A_targeted_request_reaches_only_that_console_or_none()
    {
        var (router, registry) = Create(["a", "b"]);
        Serve(registry, "a");
        Serve(registry, "b", lifecycle: CompanionLifecycle.Loading);

        var b = await router.AcquireAsync("chat", CancellationToken.None, "b");
        var a = await router.AcquireAsync("chat", CancellationToken.None, "A");

        Assert.Equal(AdmissionFailure.NoReplica, b.Failure);
        Assert.Equal("a", a.Lease!.Console.Id);
    }

    [Fact]
    public async Task An_idle_console_is_preferred_over_a_generating_one()
    {
        var (router, registry) = Create(["a", "b"]);
        Serve(registry, "a", lifecycle: CompanionLifecycle.Generating);
        Serve(registry, "b");

        var lease = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;

        Assert.Equal("b", lease.Console.Id);
    }

    [Fact]
    public async Task Unknown_alias_and_no_replica_are_reported()
    {
        var (router, _) = Create(["a"]);

        var unknown = await router.AcquireAsync("ghost", CancellationToken.None);
        var none = await router.AcquireAsync("chat", CancellationToken.None);

        Assert.Equal(AdmissionFailure.UnknownModel, unknown.Failure);
        Assert.Equal(AdmissionFailure.NoReplica, none.Failure);
        Assert.Null(none.Lease);
    }

    [Fact]
    public async Task A_busy_pool_queues_and_admits_when_a_lease_is_released()
    {
        var (router, registry) = Create(["a"]);
        Serve(registry, "a");
        var held = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;

        var waiting = router.AcquireAsync("chat", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        held.Release();
        held.Release(); // idempotent

        var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("a", admitted.Lease!.Console.Id);
        var next = router.AcquireAsync("chat", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(next.IsCompleted); // the double release did not free a second slot
        admitted.Lease.Release();
        (await next.WaitAsync(TimeSpan.FromSeconds(5))).Lease!.Release();
    }

    [Fact]
    public async Task A_full_queue_rejects_and_a_stale_wait_times_out()
    {
        var (router, registry) = Create(["a"], o =>
        {
            o.MaxQueuedRequests = 1;
            o.QueueTimeoutSeconds = 1;
        });
        Serve(registry, "a");
        using var held = new LeaseScope((await router.AcquireAsync("chat", CancellationToken.None)).Lease!);

        var queued = router.AcquireAsync("chat", CancellationToken.None);
        var rejected = await router.AcquireAsync("chat", CancellationToken.None);
        var timedOut = await queued.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AdmissionFailure.QueueFull, rejected.Failure);
        Assert.Equal(AdmissionFailure.Timeout, timedOut.Failure);
    }

    [Fact]
    public async Task A_cancelled_wait_leaves_the_queue_and_never_takes_a_slot()
    {
        var (router, registry) = Create(["a"]);
        Serve(registry, "a");
        var held = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;
        using var cts = new CancellationTokenSource();

        var first = router.AcquireAsync("chat", CancellationToken.None);
        var middle = router.AcquireAsync("chat", cts.Token);
        var last = router.AcquireAsync("chat", CancellationToken.None);
        await Task.Delay(50);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => middle);
        held.Release();

        var firstLease = (await first.WaitAsync(TimeSpan.FromSeconds(5))).Lease!;
        Assert.False(last.IsCompleted); // FIFO: the last waiter waits for the first one's slot
        firstLease.Release();
        var lastLease = (await last.WaitAsync(TimeSpan.FromSeconds(5))).Lease!;
        lastLease.Release();

        Assert.NotNull((await router.AcquireAsync("chat", CancellationToken.None)).Lease); // no slot leaked to the cancelled waiter
    }

    [Fact]
    public async Task Waiters_notice_a_replica_that_stops_serving_without_a_release()
    {
        var (router, registry) = Create(["a"]);
        Serve(registry, "a");
        var held = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;

        var waiting = router.AcquireAsync("chat", CancellationToken.None);
        Serve(registry, "a", lifecycle: CompanionLifecycle.Loading);

        Assert.Equal(AdmissionFailure.NoReplica, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
        held.Release();
    }

    [Fact]
    public async Task Waiters_fail_fast_when_the_last_replica_stops_serving()
    {
        var (router, registry) = Create(["a"]);
        Serve(registry, "a");
        var held = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;

        var waiting = router.AcquireAsync("chat", CancellationToken.None);
        Serve(registry, "a", lifecycle: CompanionLifecycle.Loading);
        held.Release();

        Assert.Equal(AdmissionFailure.NoReplica, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
    }

    private static (InferenceRouter Router, ConsoleRegistry Registry, FakeTimeProvider Time) CreateTimed(string[] consoles, Action<InferenceOptions>? configure = null)
    {
        var consoleOptions = new ConsolesOptions();
        foreach (var id in consoles)
        {
            consoleOptions.Consoles[id] = new PS5Options { Host = "127.0.0.1" };
        }

        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        var registry = new ConsoleRegistry(consoleOptions, new FakeConnectorFactory(), time);
        var options = new InferenceOptions { QueueTimeoutSeconds = 5 };
        options.Pools["chat"] = new InferencePoolOptions { Model = Model, Consoles = [.. consoles] };
        configure?.Invoke(options);
        return (new InferenceRouter(registry, options, time: time), registry, time);
    }

    private static async Task<InferenceAdmission> TurnAsync(InferenceRouter router, string key)
    {
        var admission = await router.AcquireAsync("chat", CancellationToken.None, affinityKey: key);
        admission.Lease!.Release();
        return admission;
    }

    [Fact]
    public async Task A_conversation_returns_to_its_console_although_the_other_is_least_recently_used()
    {
        var (router, registry, _) = CreateTimed(["a", "b"]);
        Serve(registry, "a");
        Serve(registry, "b");

        var first = await TurnAsync(router, "session");
        var second = await TurnAsync(router, "session");
        var third = await TurnAsync(router, "session");

        Assert.Equal(AffinityOutcome.New, first.Affinity);
        Assert.Equal(AffinityOutcome.Hit, second.Affinity);
        Assert.Equal(AffinityOutcome.Hit, third.Affinity);
        Assert.Equal(first.Lease!.Console.Id, second.Lease!.Console.Id);
        Assert.Equal(first.Lease.Console.Id, third.Lease!.Console.Id);
    }

    [Fact]
    public async Task Requests_without_a_key_keep_rotating()
    {
        var (router, registry, _) = CreateTimed(["a", "b"]);
        Serve(registry, "a");
        Serve(registry, "b");

        var first = await router.AcquireAsync("chat", CancellationToken.None);
        first.Lease!.Release();
        var second = await router.AcquireAsync("chat", CancellationToken.None);

        Assert.Equal(AffinityOutcome.None, first.Affinity);
        Assert.NotEqual(first.Lease.Console.Id, second.Lease!.Console.Id);
    }

    [Fact]
    public async Task A_busy_affine_console_sends_the_request_elsewhere_and_the_affinity_moves()
    {
        var (router, registry, _) = CreateTimed(["a", "b"]);
        Serve(registry, "a");
        Serve(registry, "b");
        var first = await TurnAsync(router, "session");
        var other = first.Lease!.Console.Id == "a" ? "b" : "a";
        var blocker = (await router.AcquireAsync("chat", CancellationToken.None, first.Lease.Console.Id)).Lease!;

        var moved = await TurnAsync(router, "session");
        blocker.Release();
        var after = await TurnAsync(router, "session");

        Assert.Equal(AffinityOutcome.Moved, moved.Affinity);
        Assert.Equal(other, moved.Lease!.Console.Id);
        Assert.Equal(AffinityOutcome.Hit, after.Affinity);
        Assert.Equal(other, after.Lease!.Console.Id);
    }

    [Fact]
    public async Task An_expired_affinity_is_forgotten_and_zero_turns_it_off()
    {
        var (router, registry, time) = CreateTimed(["a", "b"], o => o.AffinityMinutes = 10);
        Serve(registry, "a");
        Serve(registry, "b");
        await TurnAsync(router, "session");

        time.Advance(TimeSpan.FromMinutes(11));
        var expired = await TurnAsync(router, "session");

        Assert.Equal(AffinityOutcome.New, expired.Affinity);

        var (off, offRegistry, _) = CreateTimed(["a", "b"], o => o.AffinityMinutes = 0);
        Serve(offRegistry, "a");
        Serve(offRegistry, "b");
        Assert.Equal(AffinityOutcome.None, (await TurnAsync(off, "session")).Affinity);
    }

    [Fact]
    public async Task A_waiting_request_is_admitted_to_its_affine_console()
    {
        var (router, registry, _) = CreateTimed(["a", "b"]);
        Serve(registry, "a");
        Serve(registry, "b");
        var first = await TurnAsync(router, "session");
        var home = first.Lease!.Console.Id;
        var other = home == "a" ? "b" : "a";
        var holdHome = (await router.AcquireAsync("chat", CancellationToken.None, home)).Lease!;
        var holdOther = (await router.AcquireAsync("chat", CancellationToken.None, other)).Lease!;

        var waiting = router.AcquireAsync("chat", CancellationToken.None, affinityKey: "session");
        await Task.Delay(50);
        holdHome.Release();
        var admitted = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(home, admitted.Lease!.Console.Id);
        Assert.Equal(AffinityOutcome.Hit, admitted.Affinity);
        holdOther.Release();
        admitted.Lease.Release();
    }

    [Fact]
    public async Task A_generating_status_older_than_the_last_release_does_not_steer_requests_away()
    {
        var (router, registry, time) = CreateTimed(["a", "b"]);
        Serve(registry, "a");
        Serve(registry, "b");
        Publish(registry, "a", CompanionLifecycle.Generating, time.GetUtcNow());

        // "a" serves a request and is released after its status was taken: that status is stale. "b" then serves one,
        // so "a" is the least recently used console.
        var used = (await router.AcquireAsync("chat", CancellationToken.None, "a")).Lease!;
        time.Advance(TimeSpan.FromSeconds(1));
        used.Release();
        (await router.AcquireAsync("chat", CancellationToken.None, "b")).Lease!.Release();

        var stale = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;
        stale.Release();
        Assert.Equal("a", stale.Console.Id);

        // A status taken after the release that still reads Generating is outside load: "a" is passed over.
        time.Advance(TimeSpan.FromSeconds(1));
        Publish(registry, "a", CompanionLifecycle.Generating, time.GetUtcNow());
        (await router.AcquireAsync("chat", CancellationToken.None, "b")).Lease!.Release();
        var fresh = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;
        fresh.Release();

        Assert.Equal("b", fresh.Console.Id);
    }

    private static void Publish(ConsoleRegistry registry, string id, CompanionLifecycle lifecycle, DateTimeOffset at) =>
        // UpdatedAt stays old on purpose: only the time of the /metrics observation decides whether a status is stale.
        registry.Find(id)!.Store.Publish(CompanionStatus.Initial with
        {
            Lifecycle = lifecycle,
            LoadedModel = "/data/PS5LM/models/qwen.gguf",
            UpdatedAt = DateTimeOffset.MinValue,
            Telemetry = new TelemetrySnapshot(0, 0, lifecycle == CompanionLifecycle.Generating ? 1 : 0, 0, 0, at),
        });

    private sealed class LeaseScope(InferenceLease lease) : IDisposable
    {
        public void Dispose() => lease.Release();
    }
}

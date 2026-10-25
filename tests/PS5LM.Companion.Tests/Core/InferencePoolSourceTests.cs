using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class InferencePoolSourceTests
{
    [Fact]
    public void Configuration_pools_stay_read_only_and_win_over_a_stored_alias()
    {
        var warnings = new List<string>();
        var store = new MemoryPoolStore
        {
            Pools =
            [
                new StoredInferencePool("chat", "other.gguf", ["a"]),
                new StoredInferencePool("extra", "qwen.gguf", ["a"]),
            ],
        };
        var pools = Source(store, warnings, configured: true);

        Assert.Equal(InferencePoolOrigin.Configuration, pools.Find("chat")!.Origin);
        Assert.Equal("qwen.gguf", pools.Find("chat")!.Model);
        Assert.Equal(PoolEditStatus.ReadOnly, pools.Update("chat", "x.gguf", ["a"]).Status);
        Assert.Equal(PoolEditStatus.ReadOnly, pools.Remove("chat").Status);
        Assert.Equal(InferencePoolOrigin.User, pools.Find("extra")!.Origin);
        Assert.Contains("chat", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_alias_or_console_is_rejected()
    {
        var pools = Source(new MemoryPoolStore(), [], configured: false);

        Assert.Equal(PoolEditStatus.Invalid, pools.Add(null!, "qwen.gguf", ["a"]).Status);
        Assert.Equal(PoolEditStatus.Invalid, pools.Add("chat", "qwen.gguf", [null!]).Status);
        Assert.Null(pools.Find("chat"));
    }

    [Fact]
    public async Task Removing_a_pool_fails_its_waiters_and_a_granted_lease_still_releases()
    {
        var registry = Registry();
        registry.Find("a")!.Store.Publish(CompanionStatus.Initial with
        {
            Lifecycle = CompanionLifecycle.Ready,
            LoadedModel = "/data/PS5LM/models/qwen.gguf",
        });
        var options = new InferenceOptions { MaxConcurrentPerConsole = 1, QueueTimeoutSeconds = 5 };
        var pools = new HybridInferencePools(options, registry, new MemoryPoolStore());
        Assert.True(pools.Add("chat", "qwen.gguf", ["a"]).Succeeded);
        var router = new InferenceRouter(registry, options, pools);

        var held = (await router.AcquireAsync("chat", CancellationToken.None)).Lease!;
        var waiting = router.AcquireAsync("chat", CancellationToken.None);
        await Task.Delay(50);
        Assert.True(pools.Remove("chat").Succeeded);

        Assert.Equal(AdmissionFailure.UnknownModel, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Failure);
        held.Release();
        Assert.Equal(AdmissionFailure.UnknownModel, (await router.AcquireAsync("chat", CancellationToken.None)).Failure);
    }

    private static HybridInferencePools Source(MemoryPoolStore store, List<string> warnings, bool configured)
    {
        var options = new InferenceOptions();
        if (configured)
        {
            options.Pools["chat"] = new InferencePoolOptions { Model = "qwen.gguf", Consoles = ["a"] };
        }

        return new HybridInferencePools(options, Registry(), store, warnings.Add);
    }

    private static ConsoleRegistry Registry()
    {
        var consoles = new ConsolesOptions();
        consoles.Consoles["a"] = new PS5Options { Host = "127.0.0.1" };
        return new ConsoleRegistry(consoles, new FakeConnectorFactory(), TimeProvider.System);
    }

    private sealed class MemoryPoolStore : IInferencePoolStore
    {
        public List<StoredInferencePool> Pools { get; set; } = [];

        public IReadOnlyList<StoredInferencePool> Load() => Pools.ToList();

        public void Save(IReadOnlyList<StoredInferencePool> pools) => Pools = pools.ToList();
    }
}

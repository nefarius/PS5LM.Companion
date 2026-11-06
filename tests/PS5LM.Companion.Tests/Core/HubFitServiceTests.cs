using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models.Planning;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class HubFitServiceTests
{
    [Fact]
    public async Task A_file_larger_than_the_budget_is_too_big_without_a_header_request()
    {
        var hub = new HeaderHub();
        var service = new HubFitService(hub);
        var files = new[] { new HubFile("huge.gguf", 20L << 30, "abc") };

        var fits = await Collect(service.CheckAsync("acme/m", files, ModelBudget.Assumed(11.38)));

        Assert.Equal(HubFitKind.TooBig, fits.Single().Kind);
        Assert.Equal("too big: the weights alone need more than is free", fits.Single().Detail);
        Assert.Equal(0, hub.Headers);
    }

    [Fact]
    public async Task A_split_model_is_judged_from_the_sum_of_its_parts()
    {
        var hub = new HeaderHub();
        var service = new HubFitService(hub);
        var files = new[]
        {
            new HubFile("model-00001-of-00003.gguf", 4L << 30, "a"),
            new HubFile("model-00002-of-00003.gguf", 4L << 30, "b"),
            new HubFile("model-00003-of-00003.gguf", 4L << 30, "c"),
        };

        var fits = await Collect(service.CheckAsync("acme/m", files, ModelBudget.Assumed(11.38)));

        Assert.Equal(HubFitKind.TooBig, fits.Single(fit => fit.Name.Contains("00001", StringComparison.Ordinal)).Kind);
        Assert.All(fits.Where(fit => !fit.Name.Contains("00001", StringComparison.Ordinal)), fit => Assert.Equal(HubFitKind.NotApplicable, fit.Kind));
        Assert.Equal(0, hub.Headers);
    }

    [Fact]
    public async Task Two_header_checks_run_at_a_time_and_a_repeat_uses_the_hash()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hub = new HeaderHub { Hold = gate.Task };
        var service = new HubFitService(hub);
        var header = new GgufBuilder()
            .Kv("general.architecture", "llama")
            .Kv("llama.block_count", 4u)
            .Kv("llama.attention.head_count", 4u)
            .Kv("llama.embedding_length", 128u)
            .Kv("llama.context_length", 8192u)
            .Bytes();
        hub.Header = header;
        var files = new[]
        {
            new HubFile("a.gguf", 1024, "one"),
            new HubFile("b.gguf", 1024, "two"),
            new HubFile("c.gguf", 1024, "three"),
        };

        var running = Collect(service.CheckAsync("acme/m", files, ModelBudget.Assumed(11.38)));
        await hub.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HubFitService.MaxConcurrent, hub.InFlight);
        gate.SetResult();
        var fits = await running;

        Assert.Equal(3, hub.Headers);
        Assert.All(fits, fit => Assert.Equal(HubFitKind.Fits, fit.Kind));

        hub.Headers = 0;
        var again = await Collect(service.CheckAsync("acme/m", files, ModelBudget.Assumed(11.38)));
        Assert.Equal(0, hub.Headers);
        Assert.Equal(fits.Select(fit => fit.Kind), again.Select(fit => fit.Kind));
    }

    [Fact]
    public async Task A_second_file_with_the_same_hash_reuses_the_verdict()
    {
        var header = new GgufBuilder()
            .Kv("general.architecture", "llama")
            .Kv("llama.block_count", 4u)
            .Kv("llama.attention.head_count", 4u)
            .Kv("llama.embedding_length", 128u)
            .Kv("llama.context_length", 8192u)
            .Bytes();
        var hub = new HeaderHub { Header = header };
        var service = new HubFitService(hub);
        var budget = ModelBudget.Assumed(11.38);

        await Collect(service.CheckAsync("acme/m", [new HubFile("a.gguf", 1024, "same")], budget));
        var second = await Collect(service.CheckAsync("acme/m", [new HubFile("b.gguf", 1024, "same")], budget));

        Assert.Equal(1, hub.Headers);
        Assert.Equal("b.gguf", second.Single().Name);
        Assert.Equal(HubFitKind.Fits, second.Single().Kind);
    }

    [Fact]
    public async Task A_hub_that_refuses_the_header_is_unknown_and_not_remembered()
    {
        var hub = new HeaderHub { Fail = true };
        var service = new HubFitService(hub);
        var files = new[] { new HubFile("a.gguf", 1024, "abc") };

        var first = (await Collect(service.CheckAsync("acme/m", files, ModelBudget.Assumed(11.38)))).Single();
        var second = (await Collect(service.CheckAsync("acme/m", files, ModelBudget.Assumed(11.38)))).Single();

        Assert.Equal(HubFitKind.Unknown, first.Kind);
        Assert.Equal(HubFitKind.Unknown, second.Kind);
        Assert.Equal(2, hub.Headers);
    }

    [Fact]
    public async Task The_budget_is_read_from_the_state_root_and_falls_back_when_it_cannot()
    {
        var fs = new FakeFileSystem()
            .AddFile("/data/PS5LM/app.log", "device 1 Vulkan0 (GPU): 9.25 / 14.00 GiB free")
            .AddFile("/data/PS5LM/settings.json", """{"ctx_cap":32768}""");
        var service = new HubFitService(new HeaderHub(), defaultBudgetGib: 11.38);
        var root = new StorageRoot("Internal storage", "/data/PS5LM", "/data/PS5LM", Writable: true);

        var budget = await service.ReadBudgetAsync(fs, root, CancellationToken.None);
        Assert.True(budget.FromAppLog);
        Assert.Equal(9.25, budget.Gib);
        Assert.Equal(32768u, budget.CtxCap);

        fs.Unreachable = true;
        var assumed = await service.ReadBudgetAsync(fs, root, CancellationToken.None);
        Assert.False(assumed.FromAppLog);
        Assert.Equal("assumed 11.4 GiB", assumed.Caption);

        var missing = await service.ReadBudgetAsync(fs, null, CancellationToken.None);
        Assert.False(missing.FromAppLog);
    }

    private static async Task<List<HubFileFit>> Collect(IAsyncEnumerable<HubFileFit> fits)
    {
        var list = new List<HubFileFit>();
        await foreach (var fit in fits)
        {
            list.Add(fit);
        }

        return list;
    }

    private sealed class HeaderHub : IModelHub
    {
        private int _inFlight;

        public byte[] Header { get; set; } = [];

        public bool Fail { get; set; }

        public int Headers { get; set; }

        public int InFlight => _inFlight;

        public Task Hold { get; set; } = Task.CompletedTask;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<HubRepo>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HubRepo>>([]);

        public Task<IReadOnlyList<HubFile>> ListFilesAsync(string repo, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HubFile>>([]);

        public async Task<Stream> OpenHeaderAsync(string repo, string fileName, CancellationToken cancellationToken)
        {
            Headers++;
            var now = Interlocked.Increment(ref _inFlight);
            if (now == HubFitService.MaxConcurrent)
            {
                Entered.TrySetResult();
            }

            try
            {
                await Hold.WaitAsync(cancellationToken);
                if (Fail)
                {
                    throw new HubUnavailableException("no");
                }

                return new MemoryStream(Header, writable: false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public Task DownloadAsync(string repo, string fileName, string destinationPath, IProgress<long>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ConsoleSettingsServiceTests
{
    [Fact]
    public async Task An_active_transfer_blocks_a_rename_but_not_an_edit()
    {
        await using var harness = new TransferHarness();
        Assert.True(harness.Registry.Add("den", new PS5Options { Host = "10.0.0.8" }).Succeeded);
        await harness.PrimeAsync();
        var (_, transfer) = await harness.Service.EnqueueAsync(
            harness.Request("acme/tiny-GGUF", "tiny-Q4.gguf", consoleId: "den"), CancellationToken.None);
        Assert.NotNull(transfer);
        var service = Create(harness.Registry, harness.Service);

        Assert.True(service.Update("den", new PS5Options { Host = "10.0.0.9" }).Succeeded);
        var renamed = service.Update("den", new PS5Options { Host = "10.0.0.9" }, "living-room");

        Assert.Equal(ConsoleEditStatus.Conflict, renamed.Status);
        Assert.Contains("renaming", renamed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("10.0.0.9", harness.Registry.Find("den")!.Host);
        Assert.Null(harness.Registry.Find("living-room"));
    }

    [Fact]
    public void A_configuration_pool_blocks_a_rename_and_names_the_pool()
    {
        using var registry = new ConsoleRegistry(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System);
        Assert.True(registry.Add("den", new PS5Options { Host = "10.0.0.8" }).Succeeded);
        var options = new InferenceOptions();
        options.Pools["chat"] = new InferencePoolOptions { Model = "qwen.gguf", Consoles = ["den"] };
        options.Pools["other"] = new InferencePoolOptions { Model = "qwen.gguf", Consoles = ["den"] };
        var pools = new HybridInferencePools(options, registry);
        var service = Create(registry, Idle(registry), pools);

        var result = service.Update("den", new PS5Options { Host = "10.0.0.8" }, "living-room");

        Assert.Equal(ConsoleEditStatus.Conflict, result.Status);
        Assert.Contains("Inference:Pools", result.Message, StringComparison.Ordinal);
        Assert.Contains("chat", result.Message, StringComparison.Ordinal);
        Assert.Contains("other", result.Message, StringComparison.Ordinal);
        Assert.NotNull(registry.Find("den"));
        Assert.Null(registry.Find("living-room"));
    }

    private static ConsoleSettingsService Create(IConsoleRegistry registry, TransferService transfers, IInferencePoolSource? pools = null) =>
        new(registry, transfers, pools ?? new HybridInferencePools(new InferenceOptions(), registry));

    private static TransferService Idle(IConsoleRegistry registry) =>
        new(new FakeHub(), registry, new MemoryTransferStore(), new TransferOptions(), TimeProvider.System);
}

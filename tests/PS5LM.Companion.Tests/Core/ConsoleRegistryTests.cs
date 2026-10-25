using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ConsoleRegistryTests
{
    [Fact]
    public void User_consoles_can_be_added_edited_and_removed()
    {
        var store = new MemoryConsoleStore();
        using var registry = Registry(store);
        var changes = new List<ConsoleChangeKind>();
        registry.Changed += change => changes.Add(change.Kind);

        var added = registry.Add("Den", new PS5Options { Host = "10.0.0.8", DisplayName = "Den" });
        var updated = registry.Update("den", new PS5Options { Host = "10.0.0.9", DisplayName = "Den" });
        var removed = registry.Remove("DEN");

        Assert.True(added.Succeeded);
        Assert.Equal(ConsoleSource.User, updated.Console!.Source);
        Assert.Equal("10.0.0.9", updated.Console.Host);
        Assert.NotSame(added.Console, updated.Console);
        Assert.True(removed.Succeeded);
        Assert.Null(registry.Find("den"));
        Assert.Equal([ConsoleChangeKind.Added, ConsoleChangeKind.Replaced, ConsoleChangeKind.Removed], changes);
        Assert.Empty(store.Consoles);
        Assert.Equal(3, store.Saves);
    }

    [Fact]
    public void Configuration_consoles_cannot_be_changed_and_block_a_stored_id()
    {
        var warnings = new List<string>();
        var store = new MemoryConsoleStore
        {
            Consoles = [new StoredConsole("ps5", new PS5Options { Host = "10.0.0.9", DisplayName = "Stored" })],
        };
        using var registry = Registry(store, warnings, ("ps5", new PS5Options { Host = "10.0.0.1", DisplayName = "Config" }));

        var console = Assert.Single(registry.All);

        Assert.Equal(ConsoleSource.Configuration, console.Source);
        Assert.Equal("10.0.0.1", console.Host);
        Assert.Equal(ConsoleEditStatus.ReadOnly, registry.Update("ps5", new PS5Options { Host = "10.0.0.2" }).Status);
        Assert.Equal(ConsoleEditStatus.ReadOnly, registry.Remove("ps5").Status);
        Assert.Equal(ConsoleEditStatus.Conflict, registry.Add("ps5", new PS5Options { Host = "10.0.0.3" }).Status);
        Assert.Contains("ps5", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_settings_are_rejected_and_a_failed_save_rolls_back()
    {
        var store = new MemoryConsoleStore { FailNextSave = true };
        using var registry = Registry(store);

        Assert.Equal(ConsoleEditStatus.Invalid, registry.Add("nope!", new PS5Options { Host = "10.0.0.1" }).Status);
        Assert.Throws<IOException>(() => registry.Add("den", new PS5Options { Host = "10.0.0.8" }));

        Assert.Empty(registry.All);
        Assert.Empty(store.Consoles);
    }

    [Fact]
    public void A_new_registry_loads_the_consoles_the_previous_one_saved()
    {
        var store = new MemoryConsoleStore();
        using (var registry = Registry(store))
        {
            Assert.True(registry.Add("den", new PS5Options { Host = "10.0.0.8", DisplayName = "Den" }).Succeeded);
        }

        using var reloaded = Registry(store);

        var console = Assert.Single(reloaded.All);
        Assert.Equal("den", console.Id);
        Assert.Equal(ConsoleSource.User, console.Source);
        Assert.Equal("Den", console.Name);
        Assert.Equal("10.0.0.8", console.Host);
    }

    [Fact]
    public async Task A_console_added_after_the_worker_starts_gets_its_own_queue()
    {
        await using var harness = new TransferHarness();
        harness.StartWorker();
        Assert.True(harness.Registry.Add("den", new PS5Options { Host = "den" }).Succeeded);
        await harness.PrimeAsync();

        var (result, transfer) = await harness.Service.EnqueueAsync(
            harness.Request("acme/tiny-GGUF", "tiny-Q4.gguf", consoleId: "den"), CancellationToken.None);
        var finished = await harness.WaitUntilFinishedAsync(transfer!.Id);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Equal(TransferState.Completed, finished.State);
        Assert.Equal("den", finished.ConsoleId);
    }

    [Fact]
    public async Task A_queued_transfer_keeps_the_console_from_being_treated_as_idle()
    {
        await using var harness = new TransferHarness();
        await harness.PrimeAsync();

        var (_, transfer) = await harness.Service.EnqueueAsync(
            harness.Request("acme/tiny-GGUF", "tiny-Q4.gguf"), CancellationToken.None);

        Assert.NotNull(transfer);
        Assert.True(harness.Service.HasActive("ps5"));
        Assert.False(harness.Service.HasActive("den"));
    }

    private static ConsoleRegistry Registry(MemoryConsoleStore store, List<string>? warnings = null, params (string Id, PS5Options Options)[] configured)
    {
        var options = new ConsolesOptions();
        foreach (var (id, console) in configured)
        {
            options.Consoles[id] = console;
        }

        return new ConsoleRegistry(options, new FakeConnectorFactory(), TimeProvider.System, store, warnings is null ? null : warnings.Add);
    }

    private sealed class MemoryConsoleStore : IConsoleStore
    {
        public List<StoredConsole> Consoles { get; set; } = [];

        public int Saves { get; private set; }

        public bool FailNextSave { get; set; }

        public IReadOnlyList<StoredConsole> Load() => Consoles.ToList();

        public void Save(IReadOnlyList<StoredConsole> consoles)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new IOException("No space left on device.");
            }

            Saves++;
            Consoles = consoles.ToList();
        }
    }
}

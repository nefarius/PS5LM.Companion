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
        Assert.Equal(ConsoleEditStatus.ReadOnly, registry.Update("ps5", new PS5Options { Host = "10.0.0.2" }, "other").Status);
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
    public void A_user_console_can_be_renamed_and_the_old_id_is_gone()
    {
        var store = new MemoryConsoleStore();
        using var registry = Registry(store);
        ConsoleChange? last = null;
        registry.Changed += change => last = change;
        Assert.True(registry.Add("den", new PS5Options { Host = "10.0.0.8", DisplayName = "Den" }).Succeeded);
        Assert.True(registry.Add("other", new PS5Options { Host = "10.0.0.7" }).Succeeded);

        var same = registry.Update("den", new PS5Options { Host = "10.0.0.8", DisplayName = "Den" }, "DEN");
        Assert.True(same.Succeeded);
        Assert.False(last!.IsRename);

        var renamed = registry.Update("den", new PS5Options { Host = "10.0.0.9", DisplayName = "Den" }, "Living-Room");

        Assert.True(renamed.Succeeded);
        Assert.True(last.IsRename);
        Assert.Equal("den", last.Previous!.Id);
        Assert.Equal("living-room", last.Current!.Id);
        Assert.Equal("10.0.0.9", renamed.Console!.Host);
        Assert.Null(registry.Find("den"));
        Assert.Equal("Den", registry.Find("living-room")!.Name);
        Assert.Equal("living-room", store.Consoles.Single(console => console.Id != "other").Id);
        Assert.Equal(ConsoleEditStatus.Conflict, registry.Update("living-room", new PS5Options { Host = "10.0.0.9" }, "other").Status);
        Assert.Equal(ConsoleEditStatus.Invalid, registry.Update("living-room", new PS5Options { Host = "10.0.0.9" }, "Bad Id").Status);
        Assert.Equal("10.0.0.9", registry.Find("living-room")!.Host);
    }

    [Fact]
    public void A_failed_rename_keeps_the_old_id()
    {
        var store = new MemoryConsoleStore();
        using var registry = Registry(store);
        Assert.True(registry.Add("den", new PS5Options { Host = "10.0.0.8" }).Succeeded);
        store.FailNextSave = true;

        Assert.Throws<IOException>(() => registry.Update("den", new PS5Options { Host = "10.0.0.9" }, "living-room"));

        Assert.Equal("den", Assert.Single(registry.All).Id);
        Assert.Equal("10.0.0.8", registry.Find("den")!.Host);
        Assert.Equal("den", Assert.Single(store.Consoles).Id);
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
    public async Task Removing_a_console_frees_its_worker_for_the_next_one_with_the_same_id()
    {
        await using var harness = new TransferHarness();
        harness.StartWorker();
        Assert.True(harness.Registry.Add("den", new PS5Options { Host = "den" }).Succeeded);
        Assert.True(harness.Registry.Remove("den").Succeeded);
        Assert.True(harness.Registry.Add("den", new PS5Options { Host = "den" }).Succeeded);
        await harness.PrimeAsync();

        var (result, transfer) = await harness.Service.EnqueueAsync(
            harness.Request("acme/tiny-GGUF", "tiny-Q4.gguf", consoleId: "den"), CancellationToken.None);
        var finished = await harness.WaitUntilFinishedAsync(transfer!.Id);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Equal(TransferState.Completed, finished.State);
    }

    [Fact]
    public async Task Renaming_a_console_moves_its_transfer_history_and_cache()
    {
        await using var harness = new TransferHarness();
        harness.StartWorker();
        Assert.True(harness.Registry.Add("den", new PS5Options { Host = "den" }).Succeeded);
        await harness.PrimeAsync();

        var (_, transfer) = await harness.Service.EnqueueAsync(
            harness.Request("acme/tiny-GGUF", "tiny-Q4.gguf", consoleId: "den"), CancellationToken.None);
        var finished = await harness.WaitUntilFinishedAsync(transfer!.Id);
        Assert.Equal(TransferState.Completed, finished.State);

        var leftover = harness.CachedFile("acme/tiny-GGUF", "partial.gguf", "den");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        await File.WriteAllTextAsync(leftover, "partial");

        Assert.True(harness.Registry.Update("den", new PS5Options { Host = "den" }, "living-room").Succeeded);

        Assert.Equal("living-room", Assert.Single(harness.Service.Snapshot()).ConsoleId);
        Assert.Empty(harness.Service.Snapshot("den"));
        Assert.True(File.Exists(harness.CachedFile("acme/tiny-GGUF", "partial.gguf", "living-room")));
        Assert.False(Directory.Exists(Path.Combine(harness.CacheDirectory, "den")));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (harness.TransferStore.Saved.Any(item => item.ConsoleId == "den"))
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal("living-room", Assert.Single(harness.TransferStore.Saved).ConsoleId);

        await harness.PrimeAsync();
        var (result, again) = await harness.Service.EnqueueAsync(
            harness.Request("acme/tiny-GGUF", "tiny-Q4.gguf", consoleId: "living-room"), CancellationToken.None);
        var second = await harness.WaitUntilFinishedAsync(again!.Id);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Equal(TransferState.Completed, second.State);
        Assert.Equal("living-room", second.ConsoleId);
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

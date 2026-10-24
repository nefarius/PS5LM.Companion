using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class MultiConsoleTransferTests
{
    private const string Repo = "acme/tiny-GGUF";
    private const string File = "tiny-Q4.gguf";
    private const string Final = "/data/PS5LM/models/tiny-Q4.gguf";

    [Fact]
    public async Task Transfers_to_two_consoles_run_in_parallel_and_land_on_their_own_console()
    {
        await using var harness = new TransferHarness(null, "den");
        var slow = harness.Connectors["ps5"].FileSystem;
        slow.UploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        slow.UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.PrimeAsync();
        var (_, first) = await harness.Service.EnqueueAsync(harness.Request(Repo, File), CancellationToken.None);
        var (_, second) = await harness.Service.EnqueueAsync(harness.Request(Repo, File, consoleId: "den"), CancellationToken.None);
        harness.StartWorker();

        await slow.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var den = await harness.WaitUntilFinishedAsync(second!.Id);

        Assert.Equal(TransferState.Completed, den.State);
        Assert.Equal("den", den.ConsoleId);
        Assert.True(harness.Connectors["den"].FileSystem.Files.ContainsKey(Final));
        Assert.False(slow.Files.ContainsKey(Final));
        Assert.True(harness.Service.Snapshot("ps5").Single().IsActive);

        slow.UploadGate.SetResult();
        Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(first!.Id)).State);
        Assert.True(slow.Files.ContainsKey(Final));
    }

    [Fact]
    public async Task The_same_file_to_two_consoles_is_not_a_duplicate_and_the_cache_is_namespaced()
    {
        await using var harness = new TransferHarness(null, "den");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        harness.Connectors["ps5"].FileSystem.UploadGate = gate;
        harness.Connectors["ps5"].FileSystem.UploadStarted = started[0];
        harness.Connectors["den"].FileSystem.UploadGate = gate;
        harness.Connectors["den"].FileSystem.UploadStarted = started[1];
        await harness.PrimeAsync();

        var (a, _) = await harness.Service.EnqueueAsync(harness.Request(Repo, File), CancellationToken.None);
        var (b, _) = await harness.Service.EnqueueAsync(harness.Request(Repo, File, consoleId: "den"), CancellationToken.None);
        var (again, _) = await harness.Service.EnqueueAsync(harness.Request(Repo, File), CancellationToken.None);
        harness.StartWorker();
        await Task.WhenAll(started.Select(t => t.Task)).WaitAsync(TimeSpan.FromSeconds(10));

        // Both consoles are mid-upload, each with its own cached download.
        Assert.True(System.IO.File.Exists(harness.CachedFile(Repo, File)));
        Assert.True(System.IO.File.Exists(harness.CachedFile(Repo, File, "den")));
        gate.SetResult();
        foreach (var transfer in harness.Service.Snapshot())
        {
            await harness.WaitUntilFinishedAsync(transfer.Id);
        }

        Assert.Equal(OperationOutcome.Accepted, a.Outcome);
        Assert.Equal(OperationOutcome.Accepted, b.Outcome);
        Assert.Equal(OperationOutcome.Conflict, again.Outcome);
    }

    [Fact]
    public async Task An_unknown_console_is_rejected()
    {
        await using var harness = new TransferHarness();
        await harness.PrimeAsync();

        var (result, transfer) = await harness.Service.EnqueueAsync(harness.Request(Repo, File, consoleId: "ghost"), CancellationToken.None);

        Assert.Equal(OperationOutcome.NotFound, result.Outcome);
        Assert.Null(transfer);
    }

    [Fact]
    public async Task Restored_transfers_resume_on_their_own_console_and_legacy_or_orphaned_ones_fail_clearly()
    {
        await using var harness = new TransferHarness(null, "den");
        var now = DateTimeOffset.UtcNow;
        Transfer Saved(string? console) => new(Guid.NewGuid(), console!, Repo, File, FakeHub.Sha256, FakeHub.Content.Length, "Internal storage", TransferState.Queued, 0, 0, null, now, now);
        var den = Saved("den");
        var legacy = Saved(null);
        var orphan = Saved("gone");
        harness.TransferStore.Saved = [den, legacy, orphan];
        await harness.PrimeAsync();

        await harness.Service.InitializeAsync(CancellationToken.None);
        harness.StartWorker();

        Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(den.Id)).State);
        Assert.True(harness.Connectors["den"].FileSystem.Files.ContainsKey(Final));
        Assert.DoesNotContain(harness.Service.Snapshot("den"), t => t.Id == legacy.Id);
        var legacyNow = harness.Service.Snapshot().Single(t => t.Id == legacy.Id);
        var orphanNow = harness.Service.Snapshot().Single(t => t.Id == orphan.Id);
        Assert.Equal(TransferState.Failed, legacyNow.State);
        Assert.Contains("without a console id", legacyNow.Error, StringComparison.Ordinal);
        Assert.Equal(TransferState.Failed, orphanNow.State);
        Assert.Contains("no longer configured", orphanNow.Error, StringComparison.Ordinal);
    }
}

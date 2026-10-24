using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class TransferServiceTests
{
    private const string Repo = "acme/tiny-GGUF";
    private const string File = "tiny-Q4.gguf";
    private const string Final = "/data/PS5LM/models/tiny-Q4.gguf";

    private static TransferRequest Request(string repo = Repo, string file = File, string? target = null) => new("ps5", repo, file, target);

    private static async Task<(TransferHarness Harness, Transfer Transfer)> EnqueuedAsync(Action<TransferHarness>? arrange = null)
    {
        var harness = new TransferHarness();
        arrange?.Invoke(harness);
        await harness.PrimeAsync();
        var (result, transfer) = await harness.Service.EnqueueAsync(Request(), CancellationToken.None);
        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        return (harness, transfer!);
    }

    [Fact]
    public async Task A_transfer_downloads_verifies_and_uploads_through_a_part_file()
    {
        var (harness, queued) = await EnqueuedAsync();
        await using var _ = harness;
        harness.StartWorker();

        var done = await harness.WaitUntilFinishedAsync(queued.Id);

        Assert.Equal(TransferState.Completed, done.State);
        Assert.Equal(FakeHub.Content, harness.FileSystem.Files[Final]);
        Assert.DoesNotContain(harness.FileSystem.Files.Keys, k => k.EndsWith(".part", StringComparison.Ordinal));
        Assert.False(System.IO.File.Exists(harness.CachedFile(Repo, File)));
        Assert.Equal(done.SizeBytes, done.UploadedBytes);
        Assert.Equal(TransferState.Completed, harness.TransferStore.Saved.Single().State);
    }

    [Fact]
    public async Task A_hash_mismatch_discards_the_download_and_uploads_nothing()
    {
        var (harness, queued) = await EnqueuedAsync();
        await using var _ = harness;
        harness.Hub.ServedContent = [.. FakeHub.Content.Select(b => (byte)(b ^ 1))];
        harness.StartWorker();

        var done = await harness.WaitUntilFinishedAsync(queued.Id);

        Assert.Equal(TransferState.Failed, done.State);
        Assert.Contains("SHA-256", done.Error);
        Assert.False(harness.FileSystem.Files.ContainsKey(Final));
        Assert.False(System.IO.File.Exists(harness.CachedFile(Repo, File)));
    }

    [Fact]
    public async Task A_wrong_size_is_rejected_even_without_a_hash()
    {
        var (harness, queued) = await EnqueuedAsync(h => h.Hub.Files[Repo] = [new HubFile(File, FakeHub.Content.Length + 1, null)]);
        await using var _ = harness;
        harness.StartWorker();

        var done = await harness.WaitUntilFinishedAsync(queued.Id);

        Assert.Equal(TransferState.Failed, done.State);
        Assert.False(harness.FileSystem.Files.ContainsKey(Final));
    }

    [Fact]
    public async Task An_interrupted_upload_leaves_no_partial_model_and_keeps_the_verified_download()
    {
        var (harness, queued) = await EnqueuedAsync(h => h.FileSystem.FailUploads = true);
        await using var _ = harness;
        harness.StartWorker();

        var done = await harness.WaitUntilFinishedAsync(queued.Id);

        Assert.Equal(TransferState.Failed, done.State);
        Assert.DoesNotContain(harness.FileSystem.Files.Keys, k => k.Contains("tiny-Q4", StringComparison.Ordinal));
        Assert.True(System.IO.File.Exists(harness.CachedFile(Repo, File)));
    }

    [Fact]
    public async Task A_dropped_download_resumes_from_the_partial_file_on_the_next_attempt()
    {
        var (harness, queued) = await EnqueuedAsync(h => h.Hub.FailAfterBytes = 2000);
        await using var _ = harness;
        harness.StartWorker();
        var failed = await harness.WaitUntilFinishedAsync(queued.Id);
        Assert.Equal(TransferState.Failed, failed.State);

        var (retry, second) = await harness.Service.EnqueueAsync(Request(), CancellationToken.None);
        var done = await harness.WaitUntilFinishedAsync(second!.Id);

        Assert.Equal(OperationOutcome.Accepted, retry.Outcome);
        Assert.Equal(TransferState.Completed, done.State);
        Assert.Equal([0L, 2000L], harness.Hub.ResumeOffsets);
        Assert.Equal(FakeHub.Content, harness.FileSystem.Files[Final]);
    }

    [Fact]
    public async Task Too_little_free_space_fails_before_anything_is_downloaded()
    {
        var (harness, queued) = await EnqueuedAsync(h => h.Sensors.Sensors = new ConsoleSensors(null, null, null, null, [new DriveSpace("/user", 1_000_000, 1000)]));
        await using var _ = harness;
        await harness.PrimeAsync();
        harness.StartWorker();

        var done = await harness.WaitUntilFinishedAsync(queued.Id);

        Assert.Equal(TransferState.Failed, done.State);
        Assert.Contains("Not enough space", done.Error);
        Assert.Empty(harness.Hub.ResumeOffsets);
    }

    [Fact]
    public async Task Unknown_free_space_does_not_block_a_transfer()
    {
        var (harness, queued) = await EnqueuedAsync();
        await using var _ = harness;
        harness.StartWorker();

        Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(queued.Id)).State);
    }

    [Theory]
    [InlineData("nope", File)]
    [InlineData(Repo, "../x.gguf")]
    [InlineData(Repo, "readme.md")]
    public async Task Invalid_requests_are_rejected(string repo, string file)
    {
        await using var harness = new TransferHarness();
        await harness.PrimeAsync();

        var (result, transfer) = await harness.Service.EnqueueAsync(Request(repo, file), CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, result.Outcome);
        Assert.Null(transfer);
    }

    [Fact]
    public async Task A_file_the_repository_does_not_have_is_not_found()
    {
        await using var harness = new TransferHarness();
        await harness.PrimeAsync();

        var (result, _) = await harness.Service.EnqueueAsync(Request(file: "other.gguf"), CancellationToken.None);

        Assert.Equal(OperationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task A_model_already_on_the_console_is_a_conflict()
    {
        await using var harness = new TransferHarness(ConsoleFixtures.PS5LMInstalled().AddFile(Final, new byte[1]));
        await harness.PrimeAsync();

        var (result, _) = await harness.Service.EnqueueAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task The_same_file_is_not_queued_twice()
    {
        var (harness, _) = await EnqueuedAsync();
        await using var _ = harness;

        var (second, _) = await harness.Service.EnqueueAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, second.Outcome);
    }

    [Fact]
    public async Task An_unknown_storage_root_is_invalid()
    {
        await using var harness = new TransferHarness();
        await harness.PrimeAsync();

        var (result, _) = await harness.Service.EnqueueAsync(Request(target: "USB 1"), CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Transfers_are_unavailable_when_the_console_is_not_detected()
    {
        await using var harness = new TransferHarness(new FakeFileSystem());
        await harness.PrimeAsync();

        var (result, _) = await harness.Service.EnqueueAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task A_queued_transfer_can_be_cancelled_and_is_then_skipped_by_the_worker()
    {
        var (harness, queued) = await EnqueuedAsync();
        await using var _ = harness;

        var cancel = await harness.Service.CancelAsync(queued.Id, CancellationToken.None);
        harness.StartWorker();
        await Task.Delay(100);

        Assert.Equal(OperationOutcome.Accepted, cancel.Outcome);
        Assert.Equal(TransferState.Cancelled, harness.Service.Snapshot().Single().State);
        Assert.False(harness.FileSystem.Files.ContainsKey(Final));
        Assert.Empty(harness.Hub.ResumeOffsets);
    }

    [Fact]
    public async Task Cancelling_a_finished_or_unknown_transfer_reports_it()
    {
        var (harness, queued) = await EnqueuedAsync();
        await using var _ = harness;
        harness.StartWorker();
        await harness.WaitUntilFinishedAsync(queued.Id);

        Assert.Equal(OperationOutcome.NoChange, (await harness.Service.CancelAsync(queued.Id, CancellationToken.None)).Outcome);
        Assert.Equal(OperationOutcome.NotFound, (await harness.Service.CancelAsync(Guid.NewGuid(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Unfinished_transfers_are_restored_and_queued_again_after_a_restart()
    {
        var (first, queued) = await EnqueuedAsync();
        var saved = first.TransferStore.Saved.Select(t => t with { State = TransferState.Downloading }).ToList();
        await first.DisposeAsync();

        await using var second = new TransferHarness();
        second.TransferStore.Saved = saved;
        await second.PrimeAsync();
        await second.Service.InitializeAsync(CancellationToken.None);
        second.StartWorker();

        var done = await second.WaitUntilFinishedAsync(queued.Id);

        Assert.Equal(TransferState.Completed, done.State);
    }

    [Fact]
    public async Task A_failing_save_does_not_stop_the_worker_from_running_later_transfers()
    {
        var (harness, first) = await EnqueuedAsync();
        await using var _ = harness;
        harness.Hub.Files[Repo].Add(new HubFile("tiny-Q8.gguf", FakeHub.Content.Length, FakeHub.Sha256));
        var persistenceFailures = 0;
        harness.Service.PersistenceFailed += _ => Interlocked.Increment(ref persistenceFailures);
        harness.TransferStore.FailSaves = true;
        harness.StartWorker();

        await harness.WaitUntilFinishedAsync(first.Id);
        harness.TransferStore.FailSaves = false;
        var (result, second) = await harness.Service.EnqueueAsync(Request(file: "tiny-Q8.gguf"), CancellationToken.None);
        var done = await harness.WaitUntilFinishedAsync(second!.Id);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Equal(TransferState.Completed, done.State);
        Assert.True(persistenceFailures > 0);
    }

    [Fact]
    public async Task A_failed_save_while_enqueueing_leaves_nothing_behind_and_a_retry_works()
    {
        await using var harness = new TransferHarness();
        await harness.PrimeAsync();
        harness.TransferStore.FailSaves = true;

        await Assert.ThrowsAsync<IOException>(() => harness.Service.EnqueueAsync(Request(), CancellationToken.None));

        Assert.Empty(harness.Service.Snapshot());
        harness.TransferStore.FailSaves = false;
        var (result, transfer) = await harness.Service.EnqueueAsync(Request(), CancellationToken.None);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.NotNull(transfer);
    }

    [Fact]
    public async Task A_restored_transfer_can_run_before_the_first_status_refresh()
    {
        var (first, queued) = await EnqueuedAsync();
        var saved = first.TransferStore.Saved.ToList();
        await first.DisposeAsync();

        await using var second = new TransferHarness();
        second.TransferStore.Saved = saved;
        await second.Service.InitializeAsync(CancellationToken.None);
        second.StartWorker();

        Assert.Equal(TransferState.Completed, (await second.WaitUntilFinishedAsync(queued.Id)).State);
    }

    [Fact]
    public async Task A_finished_transfer_keeps_its_final_byte_counts()
    {
        var (harness, queued) = await EnqueuedAsync();
        await using var _ = harness;
        harness.StartWorker();

        var done = await harness.WaitUntilFinishedAsync(queued.Id);
        await Task.Delay(100);
        var later = harness.Service.Snapshot().Single();

        Assert.Equal(done.SizeBytes, later.UploadedBytes);
        Assert.Equal(done.UpdatedAt, later.UpdatedAt);
    }

    [Fact]
    public async Task Raises_changed_while_a_transfer_progresses()
    {
        var (harness, queued) = await EnqueuedAsync();
        await using var _ = harness;
        var changes = 0;
        harness.Service.Changed += () => Interlocked.Increment(ref changes);
        harness.StartWorker();

        await harness.WaitUntilFinishedAsync(queued.Id);

        Assert.True(changes >= 4);
    }
}

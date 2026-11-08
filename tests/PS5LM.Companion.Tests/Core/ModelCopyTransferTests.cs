using System.Text;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ModelCopyTransferTests
{
    private const string ModelPath = "/data/PS5LM/models/model.gguf";
    private const string ProjectorPath = "/data/PS5LM/models/mmproj-model.gguf";
    private static readonly byte[] ModelBytes = Encoding.UTF8.GetBytes("model-bytes");
    private static readonly byte[] ProjectorBytes = Encoding.UTF8.GetBytes("projector");

    [Fact]
    public async Task One_source_read_is_uploaded_to_every_selected_console()
    {
        var source = Installed(ModelPath, ModelBytes);
        await using var harness = new TransferHarness(source, "den", "lab");
        await harness.PrimeAsync();

        var result = await harness.Service.EnqueueCopiesAsync(Request(["den", "lab"]), CancellationToken.None);
        harness.StartWorker();

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        var ids = result.Items.Single().Targets.Select(target => target.TransferId!.Value).ToList();
        Assert.Equal(2, ids.Count);
        foreach (var id in ids)
        {
            Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(id)).State);
        }
        Assert.Equal(ModelBytes, harness.Connectors["den"].FileSystem.Files[ModelPath]);
        Assert.Equal(ModelBytes, harness.Connectors["lab"].FileSystem.Files[ModelPath]);
        Assert.Equal(ModelBytes, source.Files[ModelPath]);
        Assert.Equal(1, source.Downloads);
        Assert.DoesNotContain(harness.Connectors["den"].FileSystem.Files.Keys, path => path.EndsWith(".part", StringComparison.Ordinal));
        Assert.False(harness.Service.HasActive("ps5"));
    }

    [Fact]
    public async Task Several_models_fan_out_to_every_destination()
    {
        var source = Installed(ModelPath, ModelBytes).AddFile(ProjectorPath, ProjectorBytes);
        await using var harness = new TransferHarness(source, "den", "lab");
        await harness.PrimeAsync();

        var result = await harness.Service.EnqueueCopiesAsync(
            new CopyRequest("ps5", [ModelPath, ProjectorPath], [new("den", null), new("lab", null)]),
            CancellationToken.None);
        harness.StartWorker();

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(4, result.Items.SelectMany(item => item.Targets).Count(target => target.TransferId is not null));
        foreach (var id in result.Items.SelectMany(item => item.Targets).Select(target => target.TransferId!.Value))
        {
            Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(id)).State);
        }

        Assert.Equal(ProjectorBytes, harness.Connectors["den"].FileSystem.Files[ProjectorPath]);
        Assert.Equal(2, source.Downloads);
    }

    [Fact]
    public async Task Destinations_upload_together_while_the_source_is_read_once()
    {
        var source = Installed(ModelPath, ModelBytes);
        await using var harness = new TransferHarness(source, "den", "lab");
        var den = harness.Connectors["den"].FileSystem;
        den.UploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        den.UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.PrimeAsync();
        var result = await harness.Service.EnqueueCopiesAsync(Request(["den", "lab"]), CancellationToken.None);
        harness.StartWorker();

        await den.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var lab = result.Items.Single().Targets.Single(target => target.ConsoleId == "lab").TransferId!.Value;
        Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(lab)).State);
        Assert.False(den.Files.ContainsKey(ModelPath));
        Assert.Equal(1, source.Downloads);

        den.UploadGate.SetResult();
        var denId = result.Items.Single().Targets.Single(target => target.ConsoleId == "den").TransferId!.Value;
        Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(denId)).State);
        Assert.Equal(ModelBytes, den.Files[ModelPath]);
    }

    [Fact]
    public async Task The_same_file_name_from_another_folder_is_a_different_model()
    {
        var source = Installed(ModelPath, ModelBytes)
            .AddDirectory("/mnt/usb0")
            .AddFile("/mnt/usb0/PS5LM/models/model.gguf", ProjectorBytes);
        await using var harness = new TransferHarness(source, "den");
        await harness.PrimeAsync();

        var result = await harness.Service.EnqueueCopiesAsync(
            new CopyRequest("ps5", ["/mnt/usb0/PS5LM/models/model.gguf"], [new("den", null)]),
            CancellationToken.None);
        harness.StartWorker();

        var done = await harness.WaitUntilFinishedAsync(result.Items.Single().Targets.Single().TransferId!.Value);
        Assert.Equal(TransferState.Completed, done.State);
        Assert.Equal(ProjectorBytes, harness.Connectors["den"].FileSystem.Files[ModelPath]);
    }

    [Fact]
    public async Task Two_files_with_the_same_name_cannot_target_one_location()
    {
        var source = Installed(ModelPath, ModelBytes)
            .AddDirectory("/mnt/usb0")
            .AddFile("/mnt/usb0/PS5LM/models/model.gguf", ProjectorBytes);
        await using var harness = new TransferHarness(source, "den");
        await harness.PrimeAsync();

        var result = await harness.Service.EnqueueCopiesAsync(
            new CopyRequest("ps5", [ModelPath, "/mnt/usb0/PS5LM/models/model.gguf"], [new("den", null)]),
            CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, result.Outcome);
        Assert.Null(result.Batch);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task The_source_console_cannot_be_a_destination()
    {
        await using var harness = new TransferHarness(Installed(ModelPath, ModelBytes), "den");
        await harness.PrimeAsync();

        var result = await harness.Service.EnqueueCopiesAsync(Request(["ps5"]), CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task A_file_already_on_the_destination_is_not_replaced()
    {
        var source = Installed(ModelPath, ModelBytes);
        await using var harness = new TransferHarness(source, "den");
        harness.Connectors["den"].FileSystem.AddFile(ModelPath, new byte[] { 9 });
        await harness.PrimeAsync();

        var result = await harness.Service.EnqueueCopiesAsync(Request(["den"]), CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.Equal(CopyTargetOutcome.AlreadyPresent, result.Items.Single().Targets.Single().Outcome);
        Assert.Equal(new byte[] { 9 }, harness.Connectors["den"].FileSystem.Files[ModelPath]);
        Assert.Equal(0, source.Downloads);
    }

    [Fact]
    public async Task A_missing_destination_does_not_block_a_reachable_one()
    {
        await using var harness = new TransferHarness(Installed(ModelPath, ModelBytes), "den");
        await harness.PrimeAsync();

        var result = await harness.Service.EnqueueCopiesAsync(Request(["den", "ghost"]), CancellationToken.None);
        harness.StartWorker();

        Assert.Equal(CopyTargetOutcome.Unavailable, result.Items.Single().Targets.Single(target => target.ConsoleId == "ghost").Outcome);
        var queued = result.Items.Single().Targets.Single(target => target.ConsoleId == "den");
        Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(queued.TransferId!.Value)).State);
    }

    [Fact]
    public async Task A_selected_storage_root_is_used_when_it_is_mounted()
    {
        await using var harness = new TransferHarness(Installed(ModelPath, ModelBytes), "den");
        harness.Connectors["den"].FileSystem.AddDirectory("/mnt/usb0");
        await harness.PrimeAsync();

        var result = await harness.Service.EnqueueCopiesAsync(
            new CopyRequest("ps5", [ModelPath], [new("den", "USB 0")]),
            CancellationToken.None);
        harness.StartWorker();

        Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(result.Items.Single().Targets.Single().TransferId!.Value)).State);
        Assert.Equal(ModelBytes, harness.Connectors["den"].FileSystem.Files["/mnt/usb0/PS5LM/models/model.gguf"]);
        Assert.False(harness.Connectors["den"].FileSystem.Files.ContainsKey(ModelPath));
    }

    [Fact]
    public async Task A_short_read_fails_before_the_destination_is_changed()
    {
        var source = Installed(ModelPath, ModelBytes);
        await using var harness = new TransferHarness(source, "den");
        await harness.PrimeAsync();
        var result = await harness.Service.EnqueueCopiesAsync(Request(["den"]), CancellationToken.None);
        source.AddFile(ModelPath, new byte[3]);
        harness.StartWorker();

        var done = await harness.WaitUntilFinishedAsync(result.Items.Single().Targets.Single().TransferId!.Value);
        Assert.Equal(TransferState.Failed, done.State);
        Assert.Contains("expected", done.Error);
        Assert.False(harness.Connectors["den"].FileSystem.Files.ContainsKey(ModelPath));
    }

    [Fact]
    public async Task Cancelling_the_batch_stops_the_read_and_writes_nothing()
    {
        var source = Installed(ModelPath, ModelBytes);
        source.DownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new TransferHarness(source, "den");
        await harness.PrimeAsync();
        var result = await harness.Service.EnqueueCopiesAsync(Request(["den"]), CancellationToken.None);
        Assert.True(harness.Service.HasActive("ps5"));
        Assert.True(harness.Service.HasActive("den"));
        harness.StartWorker();
        await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelled = await harness.Service.CancelCopyAsync(result.Batch!.Id, CancellationToken.None);
        source.DownloadGate.TrySetResult();

        Assert.Equal(OperationOutcome.Accepted, cancelled.Outcome);
        var child = await harness.WaitUntilFinishedAsync(result.Items.Single().Targets.Single().TransferId!.Value);
        Assert.Equal(TransferState.Cancelled, child.State);
        Assert.False(harness.Connectors["den"].FileSystem.Files.ContainsKey(ModelPath));
        Assert.False(harness.Service.HasActive("ps5"));
    }

    [Fact]
    public async Task Cancelling_the_last_destination_stops_the_source_read()
    {
        var source = Installed(ModelPath, ModelBytes);
        source.DownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        source.DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new TransferHarness(source, "den");
        await harness.PrimeAsync();
        var result = await harness.Service.EnqueueCopiesAsync(Request(["den"]), CancellationToken.None);
        var childId = result.Items.Single().Targets.Single().TransferId!.Value;
        harness.StartWorker();
        await source.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelled = await harness.Service.CancelAsync(childId, CancellationToken.None);

        Assert.Equal(OperationOutcome.Accepted, cancelled.Outcome);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (source.ActiveDownloads > 0 || harness.Service.HasActive("ps5"))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The source read continued after its only destination was cancelled.");
            }

            await Task.Delay(20);
        }

        Assert.False(source.DownloadGate.Task.IsCompleted);
        Assert.Equal(CopyBatchState.Cancelled, harness.Service.FindCopy(result.Batch!.Id)!.Batch.State);
        Assert.Equal(TransferState.Cancelled, harness.Service.Snapshot().Single(transfer => transfer.Id == childId).State);
        Assert.False(harness.Connectors["den"].FileSystem.Files.ContainsKey(ModelPath));
    }

    [Fact]
    public async Task A_restart_finishes_a_copy_that_was_only_queued()
    {
        var source = Installed(ModelPath, ModelBytes);
        await using var harness = new TransferHarness(source, "den");
        await harness.PrimeAsync();
        var result = await harness.Service.EnqueueCopiesAsync(Request(["den"]), CancellationToken.None);
        var restarted = new TransferService(harness.Hub, harness.Registry, harness.TransferStore, harness.Options, harness.Time);
        await restarted.InitializeAsync(CancellationToken.None);
        using var stop = new CancellationTokenSource();
        var worker = restarted.RunAsync(stop.Token);

        var done = await WaitAsync(restarted, result.Items.Single().Targets.Single().TransferId!.Value);
        await stop.CancelAsync();
        await worker.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TransferState.Completed, done.State);
        Assert.Equal(ModelBytes, harness.Connectors["den"].FileSystem.Files[ModelPath]);
        Assert.Equal(1, source.Downloads);
    }

    [Fact]
    public async Task The_final_name_appears_only_after_the_part_file_is_renamed()
    {
        var source = Installed(ModelPath, ModelBytes);
        await using var harness = new TransferHarness(source, "den");
        var den = harness.Connectors["den"].FileSystem;
        den.UploadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        den.UploadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await harness.PrimeAsync();
        var result = await harness.Service.EnqueueCopiesAsync(Request(["den"]), CancellationToken.None);
        harness.StartWorker();
        await den.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(Directory.Exists(Path.Combine(harness.CacheDirectory, ".copies")));
        Assert.False(Directory.Exists(Path.Combine(harness.CacheDirectory, "copies")));
        Assert.DoesNotContain(den.Files.Keys, path => path.EndsWith(".gguf", StringComparison.Ordinal));
        den.UploadGate.SetResult();
        Assert.Equal(TransferState.Completed, (await harness.WaitUntilFinishedAsync(result.Items.Single().Targets.Single().TransferId!.Value)).State);
        Assert.Equal(ModelBytes, den.Files[ModelPath]);
    }

    [Fact]
    public void Renaming_a_console_called_copies_leaves_staged_copies_in_place()
    {
        var cache = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var registry = new ConsoleRegistry(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System);
            var service = new TransferService(new FakeHub(), registry, new MemoryTransferStore(), new TransferOptions
            {
                CacheDirectory = cache,
                StateFile = Path.Combine(cache, "transfers.json"),
            }, TimeProvider.System);
            Assert.True(registry.Add("copies", new PS5Options { Host = "10.0.0.8" }).Succeeded);
            var staged = Path.Combine(cache, ".copies", "item", "model.gguf");
            var hub = Path.Combine(cache, "copies", "acme", "model.gguf.part");
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            Directory.CreateDirectory(Path.GetDirectoryName(hub)!);
            File.WriteAllBytes(staged, [1, 2, 3]);
            File.WriteAllBytes(hub, [4]);

            var renamed = registry.Update("copies", new PS5Options { Host = "10.0.0.8" }, "living-room");

            Assert.True(renamed.Succeeded);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(staged));
            Assert.Equal(new byte[] { 4 }, File.ReadAllBytes(Path.Combine(cache, "living-room", "acme", "model.gguf.part")));
            Assert.False(Directory.Exists(Path.Combine(cache, "copies")));
            GC.KeepAlive(service);
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_restored_copy_whose_destination_was_removed_fails()
    {
        var source = Installed(ModelPath, ModelBytes);
        await using var harness = new TransferHarness(source, "den");
        await harness.PrimeAsync();
        var now = harness.Time.GetUtcNow();
        var batchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(harness.CacheDirectory, ".copies", itemId.ToString("N")));
        File.WriteAllBytes(Path.Combine(harness.CacheDirectory, ".copies", itemId.ToString("N"), "model.gguf"), ModelBytes);
        harness.TransferStore.Saved =
        [
            new Transfer(
                transferId, "gone", "ps5", "model.gguf", null, ModelBytes.Length, "Internal storage", TransferState.Staging,
                0, 0, null, now, now, TransferSourceKind.Console, batchId, itemId, "ps5", ModelPath),
        ];
        harness.TransferStore.Batches = [new CopyBatch(batchId, "ps5", [itemId], CopyBatchState.Running, now, now)];
        harness.TransferStore.Items =
        [
            new CopyItem(
                itemId, batchId, "ps5", ModelPath, ModelPath, "model.gguf", "Internal storage", ModelBytes.Length, null,
                CopyItemState.Ready, ModelBytes.Length, null,
                [new CopyTarget("gone", "Internal storage", CopyTargetOutcome.Queued, transferId, null)], now, now),
        ];

        await harness.Service.InitializeAsync(CancellationToken.None);

        var copy = harness.Service.FindCopy(batchId);
        Assert.NotNull(copy);
        Assert.Equal(CopyBatchState.CompletedWithErrors, copy.Batch.State);
        Assert.Equal(CopyItemState.Failed, copy.Items.Single().State);
        Assert.Equal(CopyTargetOutcome.Failed, copy.Items.Single().Targets.Single().Outcome);
        Assert.Contains("no longer configured", copy.Items.Single().Targets.Single().Message, StringComparison.Ordinal);
        Assert.Equal(TransferState.Failed, copy.Transfers.Single().State);
        Assert.False(harness.Service.HasActive("ps5"));
        Assert.False(harness.Connectors["den"].FileSystem.Files.ContainsKey(ModelPath));
    }

    [Fact]
    public async Task A_ready_copy_whose_destination_was_cancelled_does_not_complete()
    {
        var source = Installed(ModelPath, ModelBytes);
        await using var harness = new TransferHarness(source, "den");
        await harness.PrimeAsync();
        var now = harness.Time.GetUtcNow();
        var batchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(harness.CacheDirectory, ".copies", itemId.ToString("N")));
        File.WriteAllBytes(Path.Combine(harness.CacheDirectory, ".copies", itemId.ToString("N"), "model.gguf"), ModelBytes);
        harness.TransferStore.Saved =
        [
            new Transfer(
                transferId, "den", "ps5", "model.gguf", null, ModelBytes.Length, "Internal storage", TransferState.Cancelled,
                ModelBytes.Length, 0, null, now, now, TransferSourceKind.Console, batchId, itemId, "ps5", ModelPath),
        ];
        harness.TransferStore.Batches = [new CopyBatch(batchId, "ps5", [itemId], CopyBatchState.Running, now, now)];
        harness.TransferStore.Items =
        [
            new CopyItem(
                itemId, batchId, "ps5", ModelPath, ModelPath, "model.gguf", "Internal storage", ModelBytes.Length, null,
                CopyItemState.Ready, ModelBytes.Length, null,
                [new CopyTarget("den", "Internal storage", CopyTargetOutcome.Queued, transferId, null)], now, now),
        ];

        await harness.Service.InitializeAsync(CancellationToken.None);

        var copy = harness.Service.FindCopy(batchId);
        Assert.NotNull(copy);
        Assert.Equal(CopyBatchState.Cancelled, copy.Batch.State);
        Assert.Equal(CopyItemState.Cancelled, copy.Items.Single().State);
        Assert.Equal(CopyTargetOutcome.Cancelled, copy.Items.Single().Targets.Single().Outcome);
        Assert.False(harness.Service.HasActive("ps5"));
        Assert.False(harness.Connectors["den"].FileSystem.Files.ContainsKey(ModelPath));
    }

    private static CopyRequest Request(IReadOnlyList<string> consoles) =>
        new("ps5", [ModelPath], consoles.Select(id => new CopyTargetRequest(id, null)).ToList());

    private static FakeFileSystem Installed(string path, byte[] bytes) =>
        ConsoleFixtures.PS5LMInstalled().AddFile(path, bytes);

    private static async Task<Transfer> WaitAsync(TransferService service, Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var transfer = service.Snapshot().Single(item => item.Id == id);
            if (!transfer.IsActive)
            {
                return transfer;
            }

            await Task.Delay(10, timeout.Token);
        }
    }
}

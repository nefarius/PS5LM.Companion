using System.Security.Cryptography;
using System.Threading.Channels;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.Core.Transfers;

public sealed partial class TransferService
{
    private readonly Dictionary<Guid, CopyBatch> _batches = [];
    private readonly Dictionary<Guid, CopyItem> _items = [];
    private readonly Dictionary<string, Channel<Guid>> _stageQueues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Worker> _stageWorkers = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CancellationTokenSource> _staging = [];
    private readonly HashSet<Guid> _userCancelledItems = [];

    /// <summary>The copy batch, its items and the destination transfers created for it.</summary>
    public CopyBatchView? FindCopy(Guid batchId)
    {
        lock (_gate)
        {
            if (!_batches.TryGetValue(batchId, out var batch))
            {
                return null;
            }

            var items = batch.ItemIds.Where(_items.ContainsKey).Select(id => _items[id]).ToList();
            var transfers = _transfers.Values.Where(transfer => transfer.CopyBatchId == batchId).OrderBy(transfer => transfer.CreatedAt).ToList();
            return new CopyBatchView(batch, items, transfers);
        }
    }

    /// <summary>
    /// Stages each selected model from the source console once, then queues an upload to every destination
    /// that does not already have that file. Destinations are any configured consoles.
    /// </summary>
    public async Task<CopyEnqueueResult> EnqueueCopiesAsync(CopyRequest request, CancellationToken cancellationToken)
    {
        var sourceId = request.SourceConsoleId?.Trim() ?? string.Empty;
        var paths = (request.Paths ?? []).Select(path => path.Trim()).Where(path => path.Length > 0).ToList();
        var requested = (request.Targets ?? []).ToList();
        if (paths.Count == 0 || requested.Count == 0)
        {
            return Reject(OperationOutcome.Invalid, "Select at least one model and one console.");
        }

        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Count)
        {
            return Reject(OperationOutcome.Invalid, "The same model was selected more than once.");
        }

        if (requested.Any(target => string.IsNullOrWhiteSpace(target.ConsoleId)))
        {
            return Reject(OperationOutcome.Invalid, "Every destination needs a console.");
        }

        if (requested.Any(target => SameConsole(target.ConsoleId, sourceId)))
        {
            return Reject(OperationOutcome.Invalid, "The source console cannot be a destination.");
        }

        var destinationKeys = requested
            .Select(target => (target.ConsoleId.Trim().ToLowerInvariant(), (target.TargetRoot ?? string.Empty).Trim().ToLowerInvariant()))
            .ToList();
        if (destinationKeys.Distinct().Count() != destinationKeys.Count)
        {
            return Reject(OperationOutcome.Invalid, "A destination was selected more than once.");
        }

        var source = _consoles.Find(sourceId);
        if (source is null)
        {
            return Reject(OperationOutcome.NotFound, "That console is not configured.");
        }

        IReadOnlyList<ModelFile> listed;
        try
        {
            var installation = source.Status.LastInstallation;
            if (installation?.StateRoot is null)
            {
                return Reject(OperationOutcome.Unavailable, installation?.Message ?? "The console is not reachable.");
            }

            listed = await source.Models.ListAsync(installation, source.Store.Current.LoadedModel, cancellationToken);
        }
        catch (ConsoleUnreachableException ex)
        {
            return Reject(OperationOutcome.Unavailable, ex.Message);
        }

        var selected = new List<ModelFile>();
        foreach (var path in paths)
        {
            var match = listed.FirstOrDefault(model => model.AppPath.Equals(path, StringComparison.Ordinal));
            if (match is null)
            {
                return Reject(OperationOutcome.NotFound, $"That model is not on the console: {path}");
            }

            selected.Add(match);
        }

        var plans = new List<PlannedTarget>();
        foreach (var model in selected)
        {
            foreach (var target in requested)
            {
                plans.Add(await PlanTargetAsync(model, target, cancellationToken));
            }
        }

        var ambiguous = plans
            .Where(plan => plan.Outcome == CopyTargetOutcome.Queued)
            .GroupBy(plan => (plan.ConsoleId, plan.TargetRoot, plan.Model.FileName))
            .FirstOrDefault(group => group.Select(plan => plan.Model.AppPath).Distinct(StringComparer.Ordinal).Count() > 1);
        if (ambiguous is not null)
        {
            return Reject(
                OperationOutcome.Invalid,
                $"Two selected models would both be copied as {ambiguous.Key.FileName} on {ambiguous.Key.ConsoleId}.");
        }

        var now = _time.GetUtcNow();
        var batchId = Guid.NewGuid();
        var items = new List<CopyItem>();
        var transfers = new List<Transfer>();
        foreach (var model in selected)
        {
            var itemId = Guid.NewGuid();
            var targets = new List<CopyTarget>();
            foreach (var plan in plans.Where(plan => plan.Model.AppPath.Equals(model.AppPath, StringComparison.Ordinal)))
            {
                Guid? transferId = null;
                if (plan.Outcome == CopyTargetOutcome.Queued)
                {
                    transferId = Guid.NewGuid();
                    transfers.Add(new Transfer(
                        transferId.Value,
                        plan.ConsoleId,
                        string.Empty,
                        model.FileName,
                        null,
                        model.SizeBytes,
                        plan.TargetRoot,
                        TransferState.Staging,
                        0,
                        0,
                        null,
                        now,
                        now,
                        TransferSourceKind.Console,
                        batchId,
                        itemId,
                        source.Id,
                        model.AppPath));
                }

                targets.Add(new CopyTarget(plan.ConsoleId, plan.TargetRoot, plan.Outcome, transferId, plan.Message));
            }

            var staged = targets.Any(target => target.Outcome == CopyTargetOutcome.Queued);
            var item = new CopyItem(
                itemId,
                batchId,
                source.Id,
                model.FtpPath,
                model.AppPath,
                model.FileName,
                model.Location,
                model.SizeBytes,
                null,
                staged ? CopyItemState.Queued : ItemStateFrom(targets),
                0,
                targets.Select(target => target.Message).FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)),
                targets,
                now,
                now);
            items.Add(item);
        }

        var batchState = items.Any(item => item.State == CopyItemState.Queued)
            ? CopyBatchState.Queued
            : BatchStateFrom(items.Select(item => item.State));
        var batch = new CopyBatch(batchId, source.Id, items.Select(item => item.Id).ToList(), batchState, now, now);

        lock (_gate)
        {
            var kept = new List<Transfer>();
            foreach (var transfer in transfers)
            {
                if (_transfers.Values.Any(active => active.IsActive
                        && SameConsole(active.ConsoleId, transfer.ConsoleId)
                        && active.FileName.Equals(transfer.FileName, StringComparison.Ordinal)
                        && active.TargetRoot.Equals(transfer.TargetRoot, StringComparison.OrdinalIgnoreCase)))
                {
                    items = items.Select(item => Retarget(item, transfer.Id, CopyTargetOutcome.AlreadyQueued, "That file is already being transferred.")).ToList();
                    continue;
                }

                _transfers[transfer.Id] = transfer;
                kept.Add(transfer);
            }

            transfers = kept;
            items = items.Select(item => item.Targets.Any(target => target.Outcome == CopyTargetOutcome.Queued)
                ? item
                : item with { State = ItemStateFrom(item.Targets) }).ToList();
            batch = batch with
            {
                State = items.Any(item => item.State == CopyItemState.Queued)
                    ? CopyBatchState.Queued
                    : BatchStateFrom(items.Select(item => item.State)),
            };

            _batches[batch.Id] = batch;
            foreach (var item in items)
            {
                _items[item.Id] = item;
            }
        }

        try
        {
            await PersistAsync(cancellationToken);
        }
        catch
        {
            lock (_gate)
            {
                _batches.Remove(batch.Id);
                foreach (var item in items)
                {
                    _items.Remove(item.Id);
                }

                foreach (var transfer in transfers)
                {
                    _transfers.Remove(transfer.Id);
                }
            }

            throw;
        }

        foreach (var item in items.Where(item => item.State == CopyItemState.Queued))
        {
            QueueStage(item.SourceConsoleId, item.Id);
        }

        Changed?.Invoke();
        var outcome = transfers.Count == 0 ? OperationOutcome.Completed : OperationOutcome.Accepted;
        return new CopyEnqueueResult(outcome, null, batch, items);
    }

    /// <summary>Cancels staging and every destination upload that this batch has not finished.</summary>
    public async Task<OperationResult> CancelCopyAsync(Guid batchId, CancellationToken cancellationToken)
    {
        List<Guid> itemIds;
        List<Guid> childIds;
        lock (_gate)
        {
            if (!_batches.TryGetValue(batchId, out var batch))
            {
                return new OperationResult(OperationOutcome.NotFound, "That copy does not exist.");
            }

            if (!batch.IsActive)
            {
                return new OperationResult(OperationOutcome.NoChange);
            }

            itemIds = batch.ItemIds.ToList();
            childIds = _transfers.Values.Where(transfer => transfer.CopyBatchId == batchId && transfer.IsActive).Select(transfer => transfer.Id).ToList();
            foreach (var itemId in itemIds)
            {
                _userCancelledItems.Add(itemId);
            }

            foreach (var id in childIds)
            {
                _userCancelled.Add(id);
                if (_running.TryGetValue(id, out var running))
                {
                    running.Cancel();
                }
            }

            foreach (var itemId in itemIds)
            {
                if (_staging.TryGetValue(itemId, out var staging))
                {
                    staging.Cancel();
                }
            }
        }

        foreach (var id in childIds)
        {
            bool running;
            lock (_gate)
            {
                running = _running.ContainsKey(id);
            }

            if (!running)
            {
                Update(id, transfer => transfer.IsActive ? transfer with { State = TransferState.Cancelled } : transfer);
                RefreshCopyChild(id);
            }
        }

        foreach (var itemId in itemIds)
        {
            bool busy;
            lock (_gate)
            {
                busy = _staging.ContainsKey(itemId)
                    || ChildrenLocked(itemId).Any(transfer => _running.ContainsKey(transfer.Id));
            }

            if (!busy)
            {
                CancelItem(itemId);
            }
        }

        await PersistAsync(cancellationToken);
        return new OperationResult(OperationOutcome.Accepted);
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var state = await _store.LoadAsync(cancellationToken);
        lock (_gate)
        {
            foreach (var batch in state.CopyBatches)
            {
                _batches[batch.Id] = batch;
            }

            foreach (var item in state.CopyItems)
            {
                _items[item.Id] = item;
            }
        }

        foreach (var transfer in state.Transfers)
        {
            var restored = transfer;
            if (transfer.IsActive)
            {
                restored = string.IsNullOrEmpty(transfer.ConsoleId) || _consoles.Find(transfer.ConsoleId) is null
                    ? transfer with
                    {
                        State = TransferState.Failed,
                        Error = string.IsNullOrEmpty(transfer.ConsoleId)
                            ? "This transfer was saved without a console id and cannot be resumed. Queue it again."
                            : $"The console '{transfer.ConsoleId}' is no longer configured, so this transfer cannot be resumed.",
                    }
                    : transfer.SourceKind == TransferSourceKind.Console
                        ? transfer with { State = TransferState.Staging }
                        : transfer with { State = TransferState.Queued };
            }

            lock (_gate)
            {
                _transfers[restored.Id] = restored;
            }

            if (restored.State == TransferState.Queued)
            {
                EnsureQueue(restored.ConsoleId);
                _queues[restored.ConsoleId].Writer.TryWrite(restored.Id);
            }
        }

        foreach (var batch in _batches.Values.ToList().Where(batch => !batch.IsActive))
        {
            foreach (var itemId in batch.ItemIds)
            {
                if (_items.TryGetValue(itemId, out var item))
                {
                    TryDeleteStage(item);
                }
            }
        }

        foreach (var item in _items.Values.ToList().Where(item => item.IsActive))
        {
            await RecoverItemAsync(item, cancellationToken);
        }
    }

    private async Task RecoverItemAsync(CopyItem item, CancellationToken cancellationToken)
    {
        if (_consoles.Find(item.SourceConsoleId) is null)
        {
            FailItem(item.Id, $"The console '{item.SourceConsoleId}' is no longer configured, so this copy cannot be resumed.");
            return;
        }

        var ready = ReadyPath(item);
        if (File.Exists(ready))
        {
            var length = new FileInfo(ready).Length;
            if (item.SizeBytes <= 0 || length == item.SizeBytes)
            {
                await using var stream = File.OpenRead(ready);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (item.Sha256 is null || hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    MarkReady(item.Id, hash, length);
                    ReleaseChildren(item.Id, hash, length);
                    SettleItem(item.Id);
                    return;
                }
            }
        }

        TryDelete(ready);
        TryDelete(PartPath(item));
        UpdateItem(item.Id, current => current.IsActive
            ? current with { State = CopyItemState.Queued, StagedBytes = 0, Error = null }
            : current);
        QueueStage(item.SourceConsoleId, item.Id);
    }

    private async Task<PlannedTarget> PlanTargetAsync(ModelFile model, CopyTargetRequest request, CancellationToken cancellationToken)
    {
        var console = _consoles.Find(request.ConsoleId);
        if (console is null)
        {
            return new PlannedTarget(model, request.ConsoleId.Trim(), request.TargetRoot ?? string.Empty, CopyTargetOutcome.Unavailable, "That console is not configured.");
        }

        try
        {
            var installation = console.Status.LastInstallation;
            if (installation?.StateRoot is null)
            {
                return new PlannedTarget(model, console.Id, request.TargetRoot ?? string.Empty, CopyTargetOutcome.Unavailable, installation?.Message ?? "The console is not reachable.");
            }

            var roots = await console.Models.UploadTargetsAsync(installation, cancellationToken);
            var root = string.IsNullOrWhiteSpace(request.TargetRoot)
                ? roots.FirstOrDefault()
                : roots.FirstOrDefault(candidate => candidate.Name.Equals(request.TargetRoot, StringComparison.OrdinalIgnoreCase));
            if (root is null)
            {
                var message = string.IsNullOrWhiteSpace(request.TargetRoot)
                    ? "That console has nowhere to put a model."
                    : "That storage location is not available for uploads.";
                return new PlannedTarget(model, console.Id, request.TargetRoot ?? string.Empty, CopyTargetOutcome.InvalidTarget, message);
            }

            if (await console.FileSystem.FileExistsAsync($"{root.FtpPath.TrimEnd('/')}/{model.FileName}", cancellationToken))
            {
                return new PlannedTarget(model, console.Id, root.Name, CopyTargetOutcome.AlreadyPresent, "The console already has this file.");
            }

            bool busy;
            lock (_gate)
            {
                busy = _transfers.Values.Any(transfer => transfer.IsActive
                    && SameConsole(transfer.ConsoleId, console.Id)
                    && transfer.FileName.Equals(model.FileName, StringComparison.Ordinal)
                    && transfer.TargetRoot.Equals(root.Name, StringComparison.OrdinalIgnoreCase));
            }

            return busy
                ? new PlannedTarget(model, console.Id, root.Name, CopyTargetOutcome.AlreadyQueued, "That file is already being transferred.")
                : new PlannedTarget(model, console.Id, root.Name, CopyTargetOutcome.Queued, null);
        }
        catch (ConsoleUnreachableException ex)
        {
            return new PlannedTarget(model, console.Id, request.TargetRoot ?? string.Empty, CopyTargetOutcome.Unavailable, ex.Message);
        }
    }

    private void QueueStage(string consoleId, Guid itemId)
    {
        EnsureStageQueue(consoleId);
        Channel<Guid>? queue;
        lock (_gate)
        {
            _stageQueues.TryGetValue(consoleId, out queue);
        }

        if (queue is null || !queue.Writer.TryWrite(itemId))
        {
            FailItem(itemId, "The source console was removed before the copy could start.");
            return;
        }

        if (Volatile.Read(ref _started) == 1)
        {
            StartStageWorker(consoleId);
        }
    }

    private List<string> StageQueueIds()
    {
        lock (_gate)
        {
            return _stageQueues.Keys.ToList();
        }
    }

    private void EnsureStageQueue(string id)
    {
        lock (_gate)
        {
            if (!_stageQueues.ContainsKey(id))
            {
                _stageQueues[id] = Channel.CreateUnbounded<Guid>();
            }
        }
    }

    private void StartStageWorker(string id)
    {
        lock (_gate)
        {
            if (_stageWorkers.ContainsKey(id) || !_stageQueues.TryGetValue(id, out var channel))
            {
                return;
            }

            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            var task = RunStageAsync(id, channel, cancel);
            _stageWorkers[id] = new Worker(cancel, task);
        }
    }

    private void StopStageWorker(string id)
    {
        lock (_gate)
        {
            StopStageWorkerLocked(id);
        }
    }

    private void StopStageWorkerLocked(string id)
    {
        _stageWorkers.Remove(id, out var worker);
        _stageQueues.Remove(id, out var channel);
        worker?.Cancel.Cancel();
        channel?.Writer.TryComplete();
    }

    private void MoveStageQueue(string from, string to)
    {
        StopStageWorker(from);
        EnsureStageQueue(to);
        if (Volatile.Read(ref _started) == 1)
        {
            StartStageWorker(to);
        }
    }

    private async Task RunStageAsync(string consoleId, Channel<Guid> queue, CancellationTokenSource cancel)
    {
        await Task.Yield();
        var stoppingToken = cancel.Token;
        try
        {
            await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
            {
                CopyItem? item;
                lock (_gate)
                {
                    _items.TryGetValue(id, out item);
                }

                if (item is not { State: CopyItemState.Queued } || !SameConsole(item.SourceConsoleId, consoleId))
                {
                    continue;
                }

                await StageAsync(item, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // This console's staging loop was stopped.
        }
        finally
        {
            lock (_gate)
            {
                if (_stageWorkers.TryGetValue(consoleId, out var worker) && ReferenceEquals(worker.Cancel, cancel))
                {
                    _stageWorkers.Remove(consoleId);
                }

                if (_stageQueues.TryGetValue(consoleId, out var current) && ReferenceEquals(current, queue))
                {
                    _stageQueues.Remove(consoleId);
                }
            }
        }
    }

    private async Task StageAsync(CopyItem item, CancellationToken stoppingToken)
    {
        var part = PartPath(item);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _staging[item.Id] = cts;
        }

        try
        {
            if (Children(item.Id).All(transfer => !transfer.IsActive))
            {
                CancelItem(item.Id);
                return;
            }

            UpdateItem(item.Id, current => current with { State = CopyItemState.Staging, Error = null });
            SetBatchState(item.BatchId, CopyBatchState.Running);
            await PersistAsync(cts.Token);
            var source = _consoles.Find(item.SourceConsoleId)
                ?? throw new InvalidOperationException($"The console '{item.SourceConsoleId}' is no longer configured.");
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            await using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.FileSystem.DownloadToAsync(
                    item.SourceFtpPath,
                    stream,
                    new InlineProgress(bytes => ReportStaged(item.Id, bytes)),
                    cts.Token);
            }

            var length = new FileInfo(part).Length;
            if (item.SizeBytes > 0 && length != item.SizeBytes)
            {
                TryDelete(part);
                throw new IOException($"The copy is {length} bytes, expected {item.SizeBytes}.");
            }

            UpdateItem(item.Id, current => current with { State = CopyItemState.Verifying, StagedBytes = length });
            string hash;
            await using (var read = File.OpenRead(part))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(read, cts.Token));
            }

            var ready = ReadyPath(item);
            File.Move(part, ready, overwrite: true);
            MarkReady(item.Id, hash, length);
            ReleaseChildren(item.Id, hash, length);
        }
        catch (OperationCanceledException)
        {
            TryDelete(part);
            bool userCancelled;
            lock (_gate)
            {
                userCancelled = _userCancelledItems.Remove(item.Id);
            }

            if (userCancelled || !stoppingToken.IsCancellationRequested)
            {
                CancelItem(item.Id);
            }
            else
            {
                UpdateItem(item.Id, current => current.IsActive ? current with { State = CopyItemState.Queued, StagedBytes = 0 } : current);
            }
        }
        catch (Exception ex)
        {
            TryDelete(part);
            FailItem(item.Id, ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                _staging.Remove(item.Id);
            }
        }

        await TryPersistAsync();
    }

    private async Task ProcessCopyChildAsync(Transfer transfer, ConsoleRuntime console, CancellationToken token)
    {
        var cache = transfer.CopyItemId is Guid itemId ? ReadyFile(itemId, transfer.FileName) : null;
        try
        {
            if (cache is null || !File.Exists(cache))
            {
                throw new IOException("The staged copy is missing.");
            }

            var target = await ResolveTargetAsync(transfer, console, token);
            await EnsureSpaceAsync(transfer, console, target, token);
            Update(transfer.Id, current => current with { State = TransferState.Uploading, UploadedBytes = 0, Error = null });
            await PersistAsync(token);
            await UploadAsync(transfer, console, target, cache, token);
            Update(transfer.Id, current => current with { State = TransferState.Completed, UploadedBytes = current.SizeBytes });
        }
        catch (OperationCanceledException)
        {
            bool userCancelled;
            lock (_gate)
            {
                userCancelled = _userCancelled.Remove(transfer.Id);
            }

            Update(transfer.Id, current => current with { State = userCancelled ? TransferState.Cancelled : TransferState.Queued });
        }
        catch (Exception ex)
        {
            Update(transfer.Id, current => current with { State = TransferState.Failed, Error = ex.Message });
        }

        await TryPersistAsync();
        RefreshCopyChild(transfer.Id);
    }

    private void ReportStaged(Guid itemId, long bytes)
    {
        UpdateItem(itemId, item => item.State is CopyItemState.Staging or CopyItemState.Verifying ? item with { StagedBytes = bytes } : item);
        foreach (var child in Children(itemId).Where(transfer => transfer.State == TransferState.Staging))
        {
            Update(child.Id, transfer => transfer.State == TransferState.Staging ? transfer with { DownloadedBytes = bytes } : transfer);
        }
    }

    private void MarkReady(Guid itemId, string hash, long length)
    {
        UpdateItem(itemId, item => item.State is CopyItemState.Queued or CopyItemState.Staging or CopyItemState.Verifying or CopyItemState.Ready
            ? item with { State = CopyItemState.Ready, Sha256 = hash, StagedBytes = length, SizeBytes = length == 0 ? item.SizeBytes : length }
            : item);
    }

    private void ReleaseChildren(Guid itemId, string hash, long length)
    {
        List<Transfer> children;
        lock (_gate)
        {
            children = _transfers.Values.Where(transfer => transfer.CopyItemId == itemId && transfer.State == TransferState.Staging).ToList();
        }

        foreach (var child in children)
        {
            Update(child.Id, transfer => transfer.State == TransferState.Staging
                ? transfer with { State = TransferState.Queued, Sha256 = hash, SizeBytes = length == 0 ? transfer.SizeBytes : length, DownloadedBytes = length }
                : transfer);
            EnsureQueue(child.ConsoleId);
            Channel<Guid>? queue;
            lock (_gate)
            {
                _queues.TryGetValue(child.ConsoleId, out queue);
            }

            if (queue is null || !queue.Writer.TryWrite(child.Id))
            {
                Update(child.Id, transfer => transfer.IsActive
                    ? transfer with { State = TransferState.Failed, Error = "The destination console was removed before the upload could start." }
                    : transfer);
                RefreshCopyChild(child.Id);
            }
        }
    }

    private void RefreshCopyChild(Guid transferId)
    {
        Guid? itemId = null;
        lock (_gate)
        {
            if (_transfers.TryGetValue(transferId, out var transfer) && transfer.CopyItemId is Guid id && _items.TryGetValue(id, out var item))
            {
                itemId = id;
                var targets = item.Targets.Select(target => target.TransferId == transferId
                    ? target with { Outcome = OutcomeOf(transfer), Message = transfer.State == TransferState.Failed ? transfer.Error : target.Message }
                    : target).ToList();
                _items[id] = item with { Targets = targets, UpdatedAt = _time.GetUtcNow() };
            }
        }

        if (itemId is Guid found)
        {
            SettleItem(found);
        }
    }

    private void SettleItem(Guid itemId)
    {
        CopyItem? finished = null;
        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item) || !item.IsActive)
            {
                return;
            }

            var children = ChildrenLocked(itemId);
            if (children.Any(transfer => transfer.IsActive))
            {
                if (item.State is CopyItemState.Queued or CopyItemState.Staging or CopyItemState.Verifying
                    && children.All(transfer => transfer.State == TransferState.Cancelled))
                {
                    if (_staging.TryGetValue(itemId, out var staging))
                    {
                        _userCancelledItems.Add(itemId);
                        staging.Cancel();
                    }
                }

                return;
            }

            var state = ItemStateFrom(item.Targets);
            finished = item with { State = state, UpdatedAt = _time.GetUtcNow() };
            _items[itemId] = finished;
            SettleBatchLocked(item.BatchId);
        }

        if (finished is not null)
        {
            TryDeleteStage(finished);
            Changed?.Invoke();
        }
    }

    private void CancelItem(Guid itemId)
    {
        CopyItem? cancelled = null;
        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item) || !item.IsActive)
            {
                return;
            }

            foreach (var child in ChildrenLocked(itemId).Where(transfer => transfer.IsActive && !_running.ContainsKey(transfer.Id)))
            {
                _transfers[child.Id] = child with { State = TransferState.Cancelled, UpdatedAt = _time.GetUtcNow() };
            }

            var targets = item.Targets.Select(target =>
            {
                if (target.TransferId is Guid id && _transfers.TryGetValue(id, out var transfer))
                {
                    return target with { Outcome = OutcomeOf(transfer), Message = transfer.Error ?? target.Message };
                }

                return target.Outcome == CopyTargetOutcome.Queued
                    ? target with { Outcome = CopyTargetOutcome.Cancelled }
                    : target;
            }).ToList();
            cancelled = item with { State = CopyItemState.Cancelled, Targets = targets, UpdatedAt = _time.GetUtcNow() };
            _items[itemId] = cancelled;
            SettleBatchLocked(item.BatchId);
        }

        if (cancelled is not null)
        {
            TryDeleteStage(cancelled);
            Changed?.Invoke();
        }
    }

    private void FailItem(Guid itemId, string error)
    {
        CopyItem? failed = null;
        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item) || !item.IsActive)
            {
                return;
            }

            foreach (var child in ChildrenLocked(itemId).Where(transfer => transfer.IsActive))
            {
                _transfers[child.Id] = child with { State = TransferState.Failed, Error = error, UpdatedAt = _time.GetUtcNow() };
            }

            var targets = item.Targets.Select(target => target.Outcome == CopyTargetOutcome.Queued
                ? target with { Outcome = CopyTargetOutcome.Failed, Message = error }
                : target).ToList();
            failed = item with { State = CopyItemState.Failed, Error = error, Targets = targets, UpdatedAt = _time.GetUtcNow() };
            _items[itemId] = failed;
            SettleBatchLocked(item.BatchId);
        }

        if (failed is not null)
        {
            TryDeleteStage(failed);
            Changed?.Invoke();
        }
    }

    private void SetBatchState(Guid batchId, CopyBatchState state)
    {
        lock (_gate)
        {
            if (_batches.TryGetValue(batchId, out var batch) && batch.IsActive && batch.State != state)
            {
                _batches[batchId] = batch with { State = state, UpdatedAt = _time.GetUtcNow() };
            }
        }

        Changed?.Invoke();
    }

    private void SettleBatchLocked(Guid batchId)
    {
        if (!_batches.TryGetValue(batchId, out var batch))
        {
            return;
        }

        var states = batch.ItemIds.Where(_items.ContainsKey).Select(id => _items[id].State).ToList();
        var state = states.Any(item => item is CopyItemState.Queued or CopyItemState.Staging or CopyItemState.Verifying or CopyItemState.Ready)
            ? states.All(item => item == CopyItemState.Queued) ? CopyBatchState.Queued : CopyBatchState.Running
            : BatchStateFrom(states);
        if (batch.State != state)
        {
            _batches[batchId] = batch with { State = state, UpdatedAt = _time.GetUtcNow() };
        }
    }

    private void PruneHistoryLocked()
    {
        var finished = _transfers.Values
            .Where(transfer => !transfer.IsActive && transfer.CopyBatchId is null)
            .OrderByDescending(transfer => transfer.UpdatedAt)
            .Skip(Math.Max(0, _options.HistoryLimit))
            .Select(transfer => transfer.Id)
            .ToList();
        foreach (var id in finished)
        {
            _transfers.Remove(id);
        }

        var oldBatches = _batches.Values
            .Where(batch => !batch.IsActive)
            .OrderByDescending(batch => batch.UpdatedAt)
            .Skip(Math.Max(0, _options.CopyHistoryLimit))
            .ToList();
        foreach (var batch in oldBatches)
        {
            _batches.Remove(batch.Id);
            foreach (var itemId in batch.ItemIds)
            {
                if (_items.Remove(itemId, out var item))
                {
                    TryDeleteStage(item);
                }
            }

            foreach (var id in _transfers.Values.Where(transfer => transfer.CopyBatchId == batch.Id).Select(transfer => transfer.Id).ToList())
            {
                _transfers.Remove(id);
            }
        }
    }

    private bool RenameCopiesLocked(string from, string to)
    {
        var any = false;
        foreach (var id in _items.Keys.ToList())
        {
            var item = _items[id];
            var targets = item.Targets.Select(target => SameConsole(target.ConsoleId, from) ? target with { ConsoleId = to } : target).ToList();
            var renamedTargets = targets.Zip(item.Targets).Any(pair => !ReferenceEquals(pair.First, pair.Second) && pair.First != pair.Second);
            if (!SameConsole(item.SourceConsoleId, from) && !renamedTargets)
            {
                continue;
            }

            _items[id] = item with
            {
                SourceConsoleId = SameConsole(item.SourceConsoleId, from) ? to : item.SourceConsoleId,
                Targets = targets,
            };
            any = true;
        }

        foreach (var id in _batches.Keys.ToList())
        {
            var batch = _batches[id];
            if (!SameConsole(batch.SourceConsoleId, from))
            {
                continue;
            }

            _batches[id] = batch with { SourceConsoleId = to };
            any = true;
        }

        return any;
    }

    private List<Transfer> Children(Guid itemId)
    {
        lock (_gate)
        {
            return ChildrenLocked(itemId);
        }
    }

    private List<Transfer> ChildrenLocked(Guid itemId) =>
        _transfers.Values.Where(transfer => transfer.CopyItemId == itemId).ToList();

    private void UpdateItem(Guid id, Func<CopyItem, CopyItem> change)
    {
        var changed = false;
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var current))
            {
                return;
            }

            var next = change(current);
            if (ReferenceEquals(next, current))
            {
                return;
            }

            _items[id] = next with { UpdatedAt = _time.GetUtcNow() };
            changed = true;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private static CopyItem Retarget(CopyItem item, Guid transferId, CopyTargetOutcome outcome, string message)
    {
        var targets = item.Targets.Select(target => target.TransferId == transferId
            ? target with { Outcome = outcome, TransferId = null, Message = message }
            : target).ToList();
        return item with { Targets = targets };
    }

    private static CopyItemState ItemStateFrom(IReadOnlyList<CopyTarget> targets)
    {
        if (targets.Any(target => target.Outcome is CopyTargetOutcome.Failed or CopyTargetOutcome.Unavailable or CopyTargetOutcome.InvalidTarget))
        {
            return CopyItemState.Failed;
        }

        if (targets.Any(target => target.Outcome == CopyTargetOutcome.Cancelled)
            && targets.All(target => target.Outcome is CopyTargetOutcome.Cancelled or CopyTargetOutcome.AlreadyPresent or CopyTargetOutcome.AlreadyQueued))
        {
            return CopyItemState.Cancelled;
        }

        return CopyItemState.Completed;
    }

    private static CopyBatchState BatchStateFrom(IEnumerable<CopyItemState> states)
    {
        var list = states.ToList();
        if (list.Count == 0 || list.All(state => state == CopyItemState.Completed))
        {
            return CopyBatchState.Completed;
        }

        if (list.All(state => state == CopyItemState.Cancelled))
        {
            return CopyBatchState.Cancelled;
        }

        return CopyBatchState.CompletedWithErrors;
    }

    private static CopyTargetOutcome OutcomeOf(Transfer transfer) => transfer.State switch
    {
        TransferState.Completed => CopyTargetOutcome.Completed,
        TransferState.Cancelled => CopyTargetOutcome.Cancelled,
        TransferState.Failed => CopyTargetOutcome.Failed,
        _ => CopyTargetOutcome.Queued,
    };

    private string StageDirectory(Guid itemId) => Path.Combine(_options.CacheDirectory, "copies", itemId.ToString("N"));

    private string ReadyPath(CopyItem item) => ReadyFile(item.Id, item.FileName);

    private string ReadyFile(Guid itemId, string fileName) => Path.Combine(StageDirectory(itemId), fileName);

    private string PartPath(CopyItem item) => ReadyPath(item) + ".part";

    private void TryDeleteStage(CopyItem item)
    {
        try
        {
            var directory = StageDirectory(item.Id);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The next start removes a completed batch's cache.
        }
        catch (UnauthorizedAccessException)
        {
            // The next start removes a completed batch's cache.
        }
    }

    private static bool Involves(Transfer transfer, string consoleId) =>
        SameConsole(transfer.ConsoleId, consoleId) || SameConsole(transfer.SourceConsoleId, consoleId);

    private static bool SameConsole(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static CopyEnqueueResult Reject(OperationOutcome outcome, string message) =>
        new(outcome, message, null, []);

    private sealed record PlannedTarget(ModelFile Model, string ConsoleId, string TargetRoot, CopyTargetOutcome Outcome, string? Message);
}

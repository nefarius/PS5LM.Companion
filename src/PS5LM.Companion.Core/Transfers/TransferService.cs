using System.Security.Cryptography;
using System.Threading.Channels;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.Core.Transfers;

/// <summary>
/// Moves models from the hub to the console. The companion does the download (resumable, hash-checked) and
/// pushes the file over FTP under a <c>.part</c> name that the apps ignore, renaming it only when complete, so a
/// half-copied model is never offered to the app. Each console has its own queue and worker, so transfers to
/// different consoles run in parallel while each console's ftpsrv only ever sees one at a time.
/// </summary>
public sealed class TransferService
{
    private const string PartSuffix = ".part";

    private readonly IModelHub _hub;
    private readonly IConsoleRegistry _consoles;
    private readonly ITransferStore _store;
    private readonly TransferOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Channel<Guid>> _queues;
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private CancellationToken _stopping;
    private int _started;

    public TransferService(IModelHub hub, IConsoleRegistry consoles, ITransferStore store, TransferOptions options, TimeProvider time)
    {
        _hub = hub;
        _consoles = consoles;
        _store = store;
        _options = options;
        _time = time;
        _queues = consoles.All.ToDictionary(c => c.Id, _ => Channel.CreateUnbounded<Guid>(), StringComparer.Ordinal);
        _consoles.Changed += OnConsolesChanged;
    }

    /// <summary>Whether this console still has a queued or running transfer. Removal is refused while it does.</summary>
    public bool HasActive(string consoleId)
    {
        lock (_gate)
        {
            return _transfers.Values.Any(transfer =>
                transfer.IsActive && string.Equals(transfer.ConsoleId, consoleId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private readonly Lock _gate = new();

    // Keeps snapshots and saves in order across consoles so an older snapshot never overwrites a newer file.
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly Dictionary<Guid, Transfer> _transfers = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _running = [];
    private readonly HashSet<Guid> _userCancelled = [];
    public event Action? Changed;

    /// <summary>Raised when the transfer list could not be saved; the worker carries on.</summary>
    public event Action<Exception>? PersistenceFailed;

    /// <summary>All transfers, newest first, optionally only those of one console.</summary>
    public IReadOnlyList<Transfer> Snapshot(string? consoleId = null)
    {
        lock (_gate)
        {
            return _transfers.Values
                .Where(t => consoleId is null || string.Equals(t.ConsoleId, consoleId, StringComparison.Ordinal))
                .OrderByDescending(t => t.CreatedAt)
                .ToList();
        }
    }

    /// <summary>Restores persisted transfers; unfinished ones are queued again and resume from the cache.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var transfer in await _store.LoadAsync(cancellationToken))
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
                    : transfer with { State = TransferState.Queued };
            }

            lock (_gate)
            {
                _transfers[restored.Id] = restored;
            }

            if (restored.State == TransferState.Queued)
            {
                _queues[restored.ConsoleId].Writer.TryWrite(restored.Id);
            }
        }
    }

    public async Task<(OperationResult Result, Transfer? Transfer)> EnqueueAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        if (!ModelNames.IsValidRepoId(request.Repo) || !ModelNames.IsValidFileName(request.FileName))
        {
            return (new OperationResult(OperationOutcome.Invalid, "A valid repository id and .gguf file name are required."), null);
        }

        var runtime = _consoles.Find(request.ConsoleId);
        if (runtime is null)
        {
            return (new OperationResult(OperationOutcome.NotFound, "That console is not configured."), null);
        }

        var installation = runtime.Status.LastInstallation;
        if (installation?.StateRoot is null)
        {
            return (new OperationResult(OperationOutcome.Unavailable, installation?.Message ?? "The console is not reachable."), null);
        }

        var targets = await runtime.Models.UploadTargetsAsync(installation, cancellationToken);
        var target = request.TargetRoot is null
            ? targets.FirstOrDefault()
            : targets.FirstOrDefault(t => t.Name.Equals(request.TargetRoot, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return (new OperationResult(OperationOutcome.Invalid, "That storage location is not available for uploads."), null);
        }

        var file = (await _hub.ListFilesAsync(request.Repo, cancellationToken))
            .FirstOrDefault(f => f.Name.Equals(request.FileName, StringComparison.Ordinal));
        if (file is null)
        {
            return (new OperationResult(OperationOutcome.NotFound, "That file is not in the repository."), null);
        }

        var existing = await runtime.FileSystem.FileExistsAsync($"{target.FtpPath}/{file.Name}", cancellationToken);
        if (existing)
        {
            return (new OperationResult(OperationOutcome.Conflict, "A model with that name is already on the console."), null);
        }

        var now = _time.GetUtcNow();
        var transfer = new Transfer(
            Guid.NewGuid(), runtime.Id, request.Repo, file.Name, file.Sha256, file.SizeBytes, target.Name,
            TransferState.Queued, 0, 0, null, now, now);

        lock (_gate)
        {
            if (_transfers.Values.Any(t => t.IsActive && t.ConsoleId == transfer.ConsoleId && t.Repo == transfer.Repo && t.FileName == transfer.FileName && t.TargetRoot == transfer.TargetRoot))
            {
                return (new OperationResult(OperationOutcome.Conflict, "That file is already being transferred."), null);
            }

            _transfers[transfer.Id] = transfer;
        }

        try
        {
            await PersistAsync(cancellationToken);
        }
        catch
        {
            // Only durable transfers are queued: take the new one back out so it cannot block a retry.
            lock (_gate)
            {
                _transfers.Remove(transfer.Id);
            }

            throw;
        }

        Channel<Guid>? queue;
        lock (_gate)
        {
            _queues.TryGetValue(runtime.Id, out queue);
        }

        if (queue is null || !queue.Writer.TryWrite(transfer.Id))
        {
            Update(transfer.Id, t => t with { State = TransferState.Failed, Error = "The console was removed before the transfer could start." });
            await PersistAsync(cancellationToken);
        }

        Changed?.Invoke();
        return (new OperationResult(OperationOutcome.Accepted), transfer);
    }

    public async Task<OperationResult> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        Transfer? transfer;
        lock (_gate)
        {
            _transfers.TryGetValue(id, out transfer);
            if (transfer is { IsActive: true } && _running.TryGetValue(id, out var cts))
            {
                _userCancelled.Add(id);
                cts.Cancel();
            }
        }

        if (transfer is null)
        {
            return new OperationResult(OperationOutcome.NotFound);
        }

        if (!transfer.IsActive)
        {
            return new OperationResult(OperationOutcome.NoChange);
        }

        // A queued transfer has no running work to stop: mark it directly. A running one ends in its own handler.
        bool queued;
        lock (_gate)
        {
            queued = !_running.ContainsKey(id);
        }

        if (queued)
        {
            Update(id, t => t with { State = TransferState.Cancelled });
            await PersistAsync(cancellationToken);
        }

        return new OperationResult(OperationOutcome.Accepted);
    }

    /// <summary>
    /// The worker: one loop per console, each running one transfer at a time because that console's link and
    /// ftpsrv are the bottleneck. A stuck console never delays another. A console added later gets its own loop.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        Volatile.Write(ref _started, 1);
        foreach (var id in QueueIds())
        {
            StartWorker(id);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down. Unfinished transfers stay queued and resume on the next start.
        }

        List<Task> tasks;
        lock (_gate)
        {
            tasks = _workers.Values.Select(worker => worker.Task).ToList();
            foreach (var id in _workers.Keys.ToList())
            {
                StopWorkerLocked(id);
            }
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // A loop ended because its console was removed or the host is stopping.
        }
    }

    private void OnConsolesChanged(ConsoleChange change)
    {
        if (change.Kind is ConsoleChangeKind.Added or ConsoleChangeKind.Replaced && change.Current is { } current)
        {
            EnsureQueue(current.Id);
            if (Volatile.Read(ref _started) == 1)
            {
                StartWorker(current.Id);
            }
        }

        if (change.Kind == ConsoleChangeKind.Removed && change.Previous is { } previous)
        {
            StopWorker(previous.Id);
        }
    }

    private List<string> QueueIds()
    {
        lock (_gate)
        {
            return _queues.Keys.ToList();
        }
    }

    private void EnsureQueue(string id)
    {
        lock (_gate)
        {
            if (!_queues.ContainsKey(id))
            {
                _queues[id] = Channel.CreateUnbounded<Guid>();
            }
        }
    }

    private void StartWorker(string id)
    {
        lock (_gate)
        {
            if (_workers.ContainsKey(id) || !_queues.TryGetValue(id, out var channel))
            {
                return;
            }

            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            // Yield inside the loop, so this returns before the loop takes the lock.
            var task = RunConsoleAsync(id, channel, cancel);
            _workers[id] = new Worker(cancel, task);
        }
    }

    private void StopWorker(string id)
    {
        lock (_gate)
        {
            StopWorkerLocked(id);
        }
    }

    private void StopWorkerLocked(string id)
    {
        if (_workers.TryGetValue(id, out var worker))
        {
            worker.Cancel.Cancel();
        }

        if (_queues.TryGetValue(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    private async Task RunConsoleAsync(string consoleId, Channel<Guid> queue, CancellationTokenSource cancel)
    {
        await Task.Yield();
        var stoppingToken = cancel.Token;
        try
        {
            await foreach (var id in queue.Reader.ReadAllAsync(stoppingToken))
            {
                var console = _consoles.Find(consoleId);
                if (console is null)
                {
                    continue;
                }

                Transfer? transfer;
                lock (_gate)
                {
                    _transfers.TryGetValue(id, out transfer);
                }

                if (transfer is not { State: TransferState.Queued })
                {
                    continue;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_gate)
                {
                    _running[id] = cts;
                }

                try
                {
                    await ProcessAsync(transfer, console, cts.Token);
                }
                finally
                {
                    lock (_gate)
                    {
                        _running.Remove(id);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // This console's loop was stopped.
        }
        finally
        {
            lock (_gate)
            {
                if (_workers.TryGetValue(consoleId, out var worker) && ReferenceEquals(worker.Cancel, cancel))
                {
                    _workers.Remove(consoleId);
                }

                if (_queues.TryGetValue(consoleId, out var current) && ReferenceEquals(current, queue))
                {
                    _queues.Remove(consoleId);
                }
            }
        }
    }

    private sealed class Worker(CancellationTokenSource cancel, Task task)
    {
        public CancellationTokenSource Cancel { get; } = cancel;

        public Task Task { get; } = task;
    }

    private async Task ProcessAsync(Transfer transfer, ConsoleRuntime console, CancellationToken token)
    {
        var cachePath = CachePath(transfer);
        try
        {
            var target = await ResolveTargetAsync(transfer, console, token);
            await EnsureSpaceAsync(transfer, console, target, token);

            Update(transfer.Id, t => t with { State = TransferState.Downloading, Error = null });
            await PersistAsync(token);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await _hub.DownloadAsync(
                transfer.Repo,
                transfer.FileName,
                cachePath,
                new InlineProgress(bytes => Update(transfer.Id, t => t.State == TransferState.Downloading ? t with { DownloadedBytes = bytes } : t)),
                token);

            Update(transfer.Id, t => t with { State = TransferState.Verifying, DownloadedBytes = new FileInfo(cachePath).Length });
            await VerifyAsync(transfer, cachePath, token);

            Update(transfer.Id, t => t with { State = TransferState.Uploading, UploadedBytes = 0 });
            await PersistAsync(token);
            await UploadAsync(transfer, console, target, cachePath, token);

            File.Delete(cachePath);
            Update(transfer.Id, t => t with { State = TransferState.Completed, UploadedBytes = t.SizeBytes });
        }
        catch (OperationCanceledException)
        {
            // A user cancel is final and drops the cache. Stopping the host leaves the transfer queued,
            // with its partial download, so it resumes after a restart.
            bool userCancelled;
            lock (_gate)
            {
                userCancelled = _userCancelled.Remove(transfer.Id);
            }

            if (userCancelled)
            {
                Update(transfer.Id, t => t with { State = TransferState.Cancelled });
                TryDelete(cachePath);
            }
            else
            {
                Update(transfer.Id, t => t with { State = TransferState.Queued });
            }
        }
        catch (Exception ex)
        {
            Update(transfer.Id, t => t with { State = TransferState.Failed, Error = ex.Message });
        }

        await TryPersistAsync();
    }

    /// <summary>
    /// Saves the list without letting a disk fault stop the worker: the in-memory state stays authoritative
    /// and the next save retries.
    /// </summary>
    private async Task TryPersistAsync()
    {
        try
        {
            await PersistAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PersistenceFailed?.Invoke(ex);
        }
    }

    /// <summary>Reports on the caller's thread, so updates arrive in order (unlike <see cref="Progress{T}"/>, which posts).</summary>
    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private static async Task<StorageRoot> ResolveTargetAsync(Transfer transfer, ConsoleRuntime console, CancellationToken token)
    {
        // A restored transfer can run before the status monitor's first discovery has finished.
        if (console.Status.LastInstallation is null)
        {
            await console.Status.RefreshAsync(token);
        }

        var installation = console.Status.LastInstallation
                           ?? throw new InvalidOperationException("The console's installation is not known.");
        var targets = await console.Models.UploadTargetsAsync(installation, token);
        return targets.FirstOrDefault(t => t.Name.Equals(transfer.TargetRoot, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"The upload target '{transfer.TargetRoot}' is no longer available.");
    }

    private Task EnsureSpaceAsync(Transfer transfer, ConsoleRuntime console, StorageRoot target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var free = DriveSpaceLocator.FreeBytes(console.Store.Current.Sensors, target);

        // Without ps5-exporter the free space is unknown: let the upload try rather than refuse.
        if (free is { } bytes && bytes < transfer.SizeBytes + _options.SpaceMarginBytes)
        {
            throw new IOException(
                $"Not enough space on {target.Name}: {transfer.SizeBytes / 1048576} MiB needed plus margin, {bytes / 1048576} MiB free.");
        }

        return Task.CompletedTask;
    }

    private static async Task VerifyAsync(Transfer transfer, string path, CancellationToken token)
    {
        var length = new FileInfo(path).Length;
        if (transfer.SizeBytes > 0 && length != transfer.SizeBytes)
        {
            TryDelete(path);
            throw new IOException($"The download is {length} bytes, expected {transfer.SizeBytes}.");
        }

        if (transfer.Sha256 is null)
        {
            return;
        }

        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (!hash.Equals(transfer.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(path);
            throw new IOException("The downloaded file does not match the hub's SHA-256; it was discarded.");
        }
    }

    private async Task UploadAsync(Transfer transfer, ConsoleRuntime console, StorageRoot target, string cachePath, CancellationToken token)
    {
        var final = $"{target.FtpPath}/{transfer.FileName}";
        var part = final + PartSuffix;
        try
        {
            await using var stream = File.OpenRead(cachePath);
            await console.FileSystem.UploadAsync(
                stream,
                part,
                new InlineProgress(bytes => Update(transfer.Id, t => t.State == TransferState.Uploading ? t with { UploadedBytes = bytes } : t)),
                token);
            await console.FileSystem.MoveAsync(part, final, token);
        }
        catch
        {
            // Never leave a partial model behind on the console. The cache keeps the verified download for a retry.
            try
            {
                await console.FileSystem.DeleteAsync(part, CancellationToken.None);
            }
            catch (ConsoleUnreachableException)
            {
                // The console is gone; the leftover .part is ignored by the apps.
            }

            throw;
        }
    }

    // Partial downloads are namespaced by console so two consoles can fetch the same file at the same time.
    private string CachePath(Transfer transfer) =>
        Path.Combine(_options.CacheDirectory, transfer.ConsoleId, transfer.Repo, transfer.FileName);

    private void Update(Guid id, Func<Transfer, Transfer> change)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(id, out var current))
            {
                return;
            }

            var next = change(current);
            if (ReferenceEquals(next, current))
            {
                // A stale progress report for a state the transfer has left: nothing changed.
                return;
            }

            _transfers[id] = next with { UpdatedAt = _time.GetUtcNow() };
        }

        Changed?.Invoke();
    }

    private async Task PersistAsync(CancellationToken token)
    {
        await _persistGate.WaitAsync(token);
        try
        {
            List<Transfer> snapshot;
            lock (_gate)
            {
                var finished = _transfers.Values.Where(t => !t.IsActive).OrderByDescending(t => t.UpdatedAt).Skip(_options.HistoryLimit).Select(t => t.Id).ToList();
                foreach (var id in finished)
                {
                    _transfers.Remove(id);
                }

                snapshot = _transfers.Values.ToList();
            }

            await _store.SaveAsync(snapshot, token);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }
}

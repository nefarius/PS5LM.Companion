using System.Security.Cryptography;
using System.Threading.Channels;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.Core.Transfers;

/// <summary>
/// Moves models from the hub to the console. The companion does the download (resumable, hash-checked) and
/// pushes the file over FTP under a <c>.part</c> name that the apps ignore, renaming it only when complete, so a
/// half-copied model is never offered to the app.
/// </summary>
public sealed class TransferService(
    IModelHub hub,
    IPS5FileSystem fileSystem,
    ModelCatalog catalog,
    CompanionStatusService statusService,
    CompanionStatusStore statusStore,
    ITransferStore store,
    TransferOptions options,
    TimeProvider time)
{
    private const string PartSuffix = ".part";

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Transfer> _transfers = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _running = [];
    private readonly HashSet<Guid> _userCancelled = [];
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();

    public event Action? Changed;

    /// <summary>Raised when the transfer list could not be saved; the worker carries on.</summary>
    public event Action<Exception>? PersistenceFailed;

    public IReadOnlyList<Transfer> Snapshot()
    {
        lock (_gate)
        {
            return _transfers.Values.OrderByDescending(t => t.CreatedAt).ToList();
        }
    }

    /// <summary>Restores persisted transfers; unfinished ones are queued again and resume from the cache.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        foreach (var transfer in await store.LoadAsync(cancellationToken))
        {
            var restored = transfer.IsActive ? transfer with { State = TransferState.Queued } : transfer;
            lock (_gate)
            {
                _transfers[restored.Id] = restored;
            }

            if (restored.IsActive)
            {
                _queue.Writer.TryWrite(restored.Id);
            }
        }
    }

    public async Task<(OperationResult Result, Transfer? Transfer)> EnqueueAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        if (!ModelNames.IsValidRepoId(request.Repo) || !ModelNames.IsValidFileName(request.FileName))
        {
            return (new OperationResult(OperationOutcome.Invalid, "A valid repository id and .gguf file name are required."), null);
        }

        var installation = statusService.LastInstallation;
        if (installation?.StateRoot is null)
        {
            return (new OperationResult(OperationOutcome.Unavailable, installation?.Message ?? "The console is not reachable."), null);
        }

        var targets = await catalog.UploadTargetsAsync(installation, cancellationToken);
        var target = request.TargetRoot is null
            ? targets.FirstOrDefault()
            : targets.FirstOrDefault(t => t.Name.Equals(request.TargetRoot, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return (new OperationResult(OperationOutcome.Invalid, "That storage location is not available for uploads."), null);
        }

        var file = (await hub.ListFilesAsync(request.Repo, cancellationToken))
            .FirstOrDefault(f => f.Name.Equals(request.FileName, StringComparison.Ordinal));
        if (file is null)
        {
            return (new OperationResult(OperationOutcome.NotFound, "That file is not in the repository."), null);
        }

        var existing = await fileSystem.FileExistsAsync($"{target.FtpPath}/{file.Name}", cancellationToken);
        if (existing)
        {
            return (new OperationResult(OperationOutcome.Conflict, "A model with that name is already on the console."), null);
        }

        var now = time.GetUtcNow();
        var transfer = new Transfer(
            Guid.NewGuid(), request.Repo, file.Name, file.Sha256, file.SizeBytes, target.Name,
            TransferState.Queued, 0, 0, null, now, now);

        lock (_gate)
        {
            if (_transfers.Values.Any(t => t.IsActive && t.Repo == transfer.Repo && t.FileName == transfer.FileName && t.TargetRoot == transfer.TargetRoot))
            {
                return (new OperationResult(OperationOutcome.Conflict, "That file is already being transferred."), null);
            }

            _transfers[transfer.Id] = transfer;
        }

        await PersistAsync(cancellationToken);
        _queue.Writer.TryWrite(transfer.Id);
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

    /// <summary>The worker loop: one transfer at a time, because the console's link and ftpsrv are the bottleneck.</summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
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
                await ProcessAsync(transfer, cts.Token);
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

    private async Task ProcessAsync(Transfer transfer, CancellationToken token)
    {
        var cachePath = CachePath(transfer);
        try
        {
            var target = await ResolveTargetAsync(transfer, token);
            await EnsureSpaceAsync(transfer, target, token);

            Update(transfer.Id, t => t with { State = TransferState.Downloading, Error = null });
            await PersistAsync(token);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await hub.DownloadAsync(
                transfer.Repo,
                transfer.FileName,
                cachePath,
                new InlineProgress(bytes => Update(transfer.Id, t => t.State == TransferState.Downloading ? t with { DownloadedBytes = bytes } : t)),
                token);

            Update(transfer.Id, t => t with { State = TransferState.Verifying, DownloadedBytes = new FileInfo(cachePath).Length });
            await VerifyAsync(transfer, cachePath, token);

            Update(transfer.Id, t => t with { State = TransferState.Uploading, UploadedBytes = 0 });
            await PersistAsync(token);
            await UploadAsync(transfer, target, cachePath, token);

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

    private async Task<StorageRoot> ResolveTargetAsync(Transfer transfer, CancellationToken token)
    {
        // A restored transfer can run before the status monitor's first discovery has finished.
        if (statusService.LastInstallation is null)
        {
            await statusService.RefreshAsync(token);
        }

        var installation = statusService.LastInstallation
                           ?? throw new InvalidOperationException("The console's installation is not known.");
        var targets = await catalog.UploadTargetsAsync(installation, token);
        return targets.FirstOrDefault(t => t.Name.Equals(transfer.TargetRoot, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException($"The upload target '{transfer.TargetRoot}' is no longer available.");
    }

    private Task EnsureSpaceAsync(Transfer transfer, StorageRoot target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var free = DriveSpaceLocator.FreeBytes(statusStore.Current.Sensors, target);

        // Without ps5-exporter the free space is unknown: let the upload try rather than refuse.
        if (free is { } bytes && bytes < transfer.SizeBytes + options.SpaceMarginBytes)
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

    private async Task UploadAsync(Transfer transfer, StorageRoot target, string cachePath, CancellationToken token)
    {
        var final = $"{target.FtpPath}/{transfer.FileName}";
        var part = final + PartSuffix;
        try
        {
            await using var stream = File.OpenRead(cachePath);
            await fileSystem.UploadAsync(
                stream,
                part,
                new InlineProgress(bytes => Update(transfer.Id, t => t.State == TransferState.Uploading ? t with { UploadedBytes = bytes } : t)),
                token);
            await fileSystem.MoveAsync(part, final, token);
        }
        catch
        {
            // Never leave a partial model behind on the console. The cache keeps the verified download for a retry.
            try
            {
                await fileSystem.DeleteAsync(part, CancellationToken.None);
            }
            catch (ConsoleUnreachableException)
            {
                // The console is gone; the leftover .part is ignored by the apps.
            }

            throw;
        }
    }

    private string CachePath(Transfer transfer) =>
        Path.Combine(options.CacheDirectory, transfer.Repo, transfer.FileName);

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

            _transfers[id] = next with { UpdatedAt = time.GetUtcNow() };
        }

        Changed?.Invoke();
    }

    private async Task PersistAsync(CancellationToken token)
    {
        List<Transfer> snapshot;
        lock (_gate)
        {
            var finished = _transfers.Values.Where(t => !t.IsActive).OrderByDescending(t => t.UpdatedAt).Skip(options.HistoryLimit).Select(t => t.Id).ToList();
            foreach (var id in finished)
            {
                _transfers.Remove(id);
            }

            snapshot = _transfers.Values.ToList();
        }

        await store.SaveAsync(snapshot, token);
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

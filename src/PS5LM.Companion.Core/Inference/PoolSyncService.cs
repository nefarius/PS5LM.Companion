using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Core.Inference;

public enum PoolSyncStatus
{
    /// <summary>The pool was checked; see the per-console entries.</summary>
    Ok = 0,

    NotFound = 1,

    /// <summary>No console of the pool has the model, so there is nothing to copy.</summary>
    NoModel = 2,

    /// <summary>A console has the model but where it came from is unknown.</summary>
    NoSource = 3,
}

public enum PoolSyncOutcome
{
    /// <summary>The download was queued on this console.</summary>
    Queued = 0,

    /// <summary>The console has the model, or the same download is already running.</summary>
    AlreadyThere = 1,

    Failed = 2,
}

public sealed record PoolSyncEntry(string ConsoleId, PoolSyncOutcome Outcome, string? Message);

public sealed record PoolSyncResult(PoolSyncStatus Status, string? Message, ModelSource? Source, IReadOnlyList<PoolSyncEntry> Consoles);

/// <summary>
/// Brings a pool's consoles to the same model: finds one that has the pool's model, learns where it was
/// downloaded from, and queues the identical download on every other console of the pool.
/// </summary>
public sealed class PoolSyncService(IInferencePoolSource pools, IConsoleRegistry consoles, TransferService transfers)
{
    public async Task<PoolSyncResult> SyncAsync(string alias, CancellationToken cancellationToken)
    {
        var pool = pools.Find(alias);
        if (pool is null)
        {
            return new PoolSyncResult(PoolSyncStatus.NotFound, "That pool does not exist.", null, []);
        }

        var entries = new Dictionary<string, PoolSyncEntry>(StringComparer.OrdinalIgnoreCase);
        var holders = new List<(ConsoleRuntime Console, ModelFile File)>();
        var missing = new List<ConsoleRuntime>();

        foreach (var id in pool.Consoles)
        {
            var runtime = consoles.Find(id);
            if (runtime is null)
            {
                entries[id] = new PoolSyncEntry(id, PoolSyncOutcome.Failed, "That console is not configured.");
                continue;
            }

            try
            {
                var installation = runtime.Status.LastInstallation;
                if (installation?.StateRoot is null)
                {
                    entries[id] = new PoolSyncEntry(id, PoolSyncOutcome.Failed, installation?.Message ?? "The console is not reachable.");
                    continue;
                }

                var models = await runtime.Models.ListAsync(installation, runtime.Store.Current.LoadedModel, cancellationToken);
                var match = models.FirstOrDefault(m => !m.IsProjector && ModelNames.MatchesConfigured(m.AppPath, pool.Model));
                if (match is null)
                {
                    missing.Add(runtime);
                }
                else
                {
                    holders.Add((runtime, match));
                    entries[id] = new PoolSyncEntry(id, PoolSyncOutcome.AlreadyThere, null);
                }
            }
            catch (ConsoleUnreachableException ex)
            {
                entries[id] = new PoolSyncEntry(id, PoolSyncOutcome.Failed, ex.Message);
            }
        }

        if (holders.Count == 0)
        {
            return new PoolSyncResult(PoolSyncStatus.NoModel, $"No console in this pool has {pool.Model}, so there is nothing to copy.", null, Ordered(pool, entries));
        }

        if (missing.Count == 0)
        {
            return new PoolSyncResult(PoolSyncStatus.Ok, null, null, Ordered(pool, entries));
        }

        var source = await FindSourceAsync(holders, cancellationToken);
        if (source is null)
        {
            return new PoolSyncResult(
                PoolSyncStatus.NoSource,
                $"Where {holders[0].File.FileName} came from is not recorded. Download it once through the companion or PS5LM's own download list, then try again.",
                null,
                Ordered(pool, entries));
        }

        foreach (var runtime in missing)
        {
            entries[runtime.Id] = await QueueAsync(runtime, source, pool.Model, cancellationToken);
        }

        return new PoolSyncResult(PoolSyncStatus.Ok, null, source, Ordered(pool, entries));
    }

    private async Task<ModelSource?> FindSourceAsync(List<(ConsoleRuntime Console, ModelFile File)> holders, CancellationToken cancellationToken)
    {
        // The console's own history first: it records exactly what PS5LM downloaded.
        foreach (var (console, file) in holders)
        {
            if (console.Status.LastInstallation?.StateRoot is not { } stateRoot)
            {
                continue;
            }

            try
            {
                var recorded = await ModelSources.ReadAsync(console.FileSystem, stateRoot, cancellationToken);
                if (recorded.FirstOrDefault(s => s.FileName.Equals(file.FileName, StringComparison.Ordinal)) is { } source)
                {
                    return source;
                }
            }
            catch (ConsoleUnreachableException)
            {
                // Try the next console that has the model.
            }
        }

        // Then the downloads this companion made itself.
        foreach (var (_, file) in holders)
        {
            var done = transfers.Snapshot().FirstOrDefault(t => t.State == TransferState.Completed && t.FileName.Equals(file.FileName, StringComparison.Ordinal));
            if (done is not null)
            {
                return new ModelSource(done.Repo, done.FileName, done.Sha256, done.SizeBytes);
            }
        }

        return null;
    }

    private async Task<PoolSyncEntry> QueueAsync(ConsoleRuntime runtime, ModelSource source, string configuredModel, CancellationToken cancellationToken)
    {
        try
        {
            // A model configured with a path must land on the storage root that path points into.
            string? targetRoot = null;
            var wanted = configuredModel.Trim().Replace('\\', '/');
            if (wanted.Contains('/', StringComparison.Ordinal))
            {
                var installation = runtime.Status.LastInstallation;
                var targets = installation is null ? [] : await runtime.Models.UploadTargetsAsync(installation, cancellationToken);
                targetRoot = targets.FirstOrDefault(t => $"{t.AppPath.TrimEnd('/')}/{source.FileName}".Equals(wanted, StringComparison.OrdinalIgnoreCase))?.Name;
                if (targetRoot is null)
                {
                    return new PoolSyncEntry(runtime.Id, PoolSyncOutcome.Failed, "The storage location of the configured model is not available for downloads.");
                }
            }

            var (result, _) = await transfers.EnqueueAsync(new TransferRequest(runtime.Id, source.Repo, source.FileName, targetRoot), cancellationToken);
            return result.Outcome switch
            {
                _ when result.Succeeded => new PoolSyncEntry(runtime.Id, PoolSyncOutcome.Queued, null),
                OperationOutcome.Conflict => new PoolSyncEntry(runtime.Id, PoolSyncOutcome.AlreadyThere, result.Message),
                _ => new PoolSyncEntry(runtime.Id, PoolSyncOutcome.Failed, result.Message),
            };
        }
        catch (Exception ex) when (ex is ConsoleUnreachableException or HubUnavailableException or IOException or UnauthorizedAccessException)
        {
            return new PoolSyncEntry(runtime.Id, PoolSyncOutcome.Failed, ex.Message);
        }
    }

    private static List<PoolSyncEntry> Ordered(InferencePool pool, Dictionary<string, PoolSyncEntry> entries) =>
        pool.Consoles.Where(entries.ContainsKey).Select(id => entries[id]).ToList();
}

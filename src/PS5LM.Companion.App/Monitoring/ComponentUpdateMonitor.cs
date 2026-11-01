using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Core.Updates;

namespace PS5LM.Companion.App.Monitoring;

/// <summary>
/// Runs the pinned-manifest hash check on each console. The first pass starts once the console is reachable,
/// then it repeats every <c>UpdateCheckIntervalMinutes</c>. A live version change also requests an early pass.
/// Disconnected or loading consoles, and consoles with an active transfer, are deferred because ftpsrv is busy.
/// </summary>
public sealed partial class ComponentUpdateMonitor(
    IConsoleRegistry consoles,
    TransferService transfers,
    ILogger<ComponentUpdateMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    private readonly Lock _loopsGate = new();
    private readonly Dictionary<string, CancellationTokenSource> _loops = new(StringComparer.Ordinal);
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        consoles.Changed += OnChanged;
        foreach (var console in consoles.All)
        {
            Start(console);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            consoles.Changed -= OnChanged;
            List<CancellationTokenSource> loops;
            lock (_loopsGate)
            {
                loops = _loops.Values.ToList();
                _loops.Clear();
            }

            foreach (var loop in loops)
            {
                try
                {
                    await loop.CancelAsync();
                }
                catch (ObjectDisposedException)
                {
                    // That loop already ended and disposed its source.
                }
            }
        }
    }

    private void OnChanged(ConsoleChange change)
    {
        if (change.Kind is ConsoleChangeKind.Removed or ConsoleChangeKind.Replaced && change.Previous is { } previous)
        {
            Stop(previous.Id);
        }

        if (change.Kind is ConsoleChangeKind.Added or ConsoleChangeKind.Replaced && change.Current is { } current)
        {
            Start(current);
        }
    }

    private void Start(ConsoleRuntime console)
    {
        if (console.Updates is null || console.Options.UpdateCheckIntervalMinutes <= 0)
        {
            return;
        }

        var loop = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        lock (_loopsGate)
        {
            if (!_loops.TryAdd(console.Id, loop))
            {
                loop.Dispose();
                return;
            }
        }

        _ = MonitorAsync(console, console.Updates, loop);
    }

    private void Stop(string id)
    {
        CancellationTokenSource? loop;
        lock (_loopsGate)
        {
            _loops.Remove(id, out loop);
        }

        loop?.Cancel();
    }

    private async Task MonitorAsync(ConsoleRuntime console, ComponentUpdateService updates, CancellationTokenSource loop)
    {
        using var scope = logger.BeginScope("Console {ConsoleId}", console.Id);
        DateTimeOffset? lastCheck = null;
        try
        {
            while (!loop.IsCancellationRequested)
            {
                if (!CanCheck(console.Store.Current, transfers.HasActive(console.Id)))
                {
                    await DelayAsync(Poll, loop.Token);
                    continue;
                }

                var interval = TimeSpan.FromMinutes(console.Options.UpdateCheckIntervalMinutes);
                var due = lastCheck is null || updates.IsDue
                    || DateTimeOffset.UtcNow - lastCheck.Value >= interval;
                if (!due && lastCheck is { } checkedAt)
                {
                    var remaining = interval - (DateTimeOffset.UtcNow - checkedAt);
                    await WaitDueOrDelayAsync(updates, remaining > TimeSpan.Zero ? remaining : Poll, loop.Token);
                    continue;
                }

                updates.ClearDue();
                try
                {
                    await updates.CheckAsync(loop.Token);
                    lastCheck = DateTimeOffset.UtcNow;
                }
                catch (OperationCanceledException) when (loop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogCheckFailed(ex, console.Id);
                    lastCheck = DateTimeOffset.UtcNow;
                }
            }
        }
        finally
        {
            loop.Dispose();
        }
    }

    /// <summary>FTP is used for the hash check; skip while the console is down, loading a model, or receiving a transfer.</summary>
    internal static bool CanCheck(CompanionStatus status, bool transferActive) =>
        status.UpdatedAt != DateTimeOffset.MinValue
        && status.Lifecycle is not CompanionLifecycle.Disconnected and not CompanionLifecycle.Loading
        && !transferActive;

    private static async Task WaitDueOrDelayAsync(ComponentUpdateService updates, TimeSpan delay, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(delay);
        try
        {
            await updates.WaitDueAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Interval elapsed.
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
            // Loop is stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking pinned versions on console {ConsoleId} failed")]
    private partial void LogCheckFailed(Exception exception, string consoleId);
}

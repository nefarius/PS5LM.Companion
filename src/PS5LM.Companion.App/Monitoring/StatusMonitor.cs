using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.App.Monitoring;

/// <summary>
/// Refreshes every console's status on its own timer. Each console has its own loop, so a slow or unreachable
/// console never delays another one. Loops start and stop as consoles are added and removed.
/// </summary>
public sealed partial class StatusMonitor(IConsoleRegistry consoles, ILogger<StatusMonitor> logger) : BackgroundService
{
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
        var loop = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        lock (_loopsGate)
        {
            if (!_loops.TryAdd(console.Id, loop))
            {
                loop.Dispose();
                return;
            }
        }

        _ = MonitorAsync(console, loop);
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

    private async Task MonitorAsync(ConsoleRuntime console, CancellationTokenSource loop)
    {
        using var scope = logger.BeginScope("Console {ConsoleId}", console.Id);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(console.Options.PollIntervalSeconds));
        CompanionLifecycle? previous = null;
        try
        {
            do
            {
                try
                {
                    var status = await console.Status.RefreshAsync(loop.Token);
                    console.Store.Publish(status);
                    LogTransition(console, previous, status);
                    previous = status.Lifecycle;
                }
                catch (OperationCanceledException) when (loop.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogRefreshFailed(ex, console.Id);
                }
            }
            while (await WaitAsync(timer, loop.Token));
        }
        finally
        {
            loop.Dispose();
        }
    }

    /// <summary>Logs the moments the console drops out and comes back, with the reason the status gives.</summary>
    private void LogTransition(ConsoleRuntime console, CompanionLifecycle? previous, CompanionStatus status)
    {
        if (previous == status.Lifecycle)
        {
            return;
        }

        if (status.Lifecycle == CompanionLifecycle.Disconnected)
        {
            LogDisconnected(console.Id, console.Host, previous?.ToString() ?? "unknown", status.Message ?? "no reason given");
        }
        else if (previous == CompanionLifecycle.Disconnected)
        {
            LogReconnected(console.Id, console.Host, status.Lifecycle.ToString());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Console {ConsoleId} ({Host}) is disconnected (was {Previous}): {Reason}")]
    private partial void LogDisconnected(string consoleId, string host, string previous, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Console {ConsoleId} ({Host}) is reachable again: {Lifecycle}")]
    private partial void LogReconnected(string consoleId, string host, string lifecycle);

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing the status of console {ConsoleId} failed")]
    private partial void LogRefreshFailed(Exception exception, string consoleId);
}

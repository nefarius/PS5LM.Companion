using PS5LM.Companion.Core.Consoles;

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
                await loop.CancelAsync();
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
        try
        {
            do
            {
                try
                {
                    console.Store.Publish(await console.Status.RefreshAsync(loop.Token));
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

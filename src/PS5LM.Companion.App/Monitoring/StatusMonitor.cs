using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.App.Monitoring;

/// <summary>
/// Refreshes every console's status on its own timer. Each console has its own loop, so a slow or unreachable
/// console never delays another one.
/// </summary>
public sealed partial class StatusMonitor(IConsoleRegistry consoles, ILogger<StatusMonitor> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(consoles.All.Select(console => MonitorAsync(console, stoppingToken)));

    private async Task MonitorAsync(ConsoleRuntime console, CancellationToken stoppingToken)
    {
        using var scope = logger.BeginScope("Console {ConsoleId}", console.Id);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(console.Options.PollIntervalSeconds));
        do
        {
            try
            {
                console.Store.Publish(await console.Status.RefreshAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRefreshFailed(ex, console.Id);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
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

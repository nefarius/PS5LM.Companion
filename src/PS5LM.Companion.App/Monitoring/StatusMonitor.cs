using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.App.Monitoring;

/// <summary>Refreshes the console status on a timer and publishes it to the store.</summary>
public sealed partial class StatusMonitor(
    CompanionStatusService service,
    CompanionStatusStore store,
    PS5Options options,
    ILogger<StatusMonitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollIntervalSeconds));
        do
        {
            try
            {
                store.Publish(await service.RefreshAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRefreshFailed(ex);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing the console status failed")]
    private partial void LogRefreshFailed(Exception exception);
}

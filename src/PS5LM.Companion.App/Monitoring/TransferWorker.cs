using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Monitoring;

/// <summary>Restores persisted transfers and runs the transfer queue.</summary>
public sealed partial class TransferWorker(TransferService service, ILogger<TransferWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        service.PersistenceFailed += LogPersistenceFailed;
        try
        {
            await service.InitializeAsync(stoppingToken);
            await service.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down; unfinished transfers stay queued and resume on the next start.
        }
        catch (Exception ex)
        {
            LogWorkerStopped(ex);
        }
        finally
        {
            service.PersistenceFailed -= LogPersistenceFailed;
        }
    }

    private void LogPersistenceFailed(Exception exception) => LogSaveFailed(exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the transfer list; the next change retries")]
    private partial void LogSaveFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The transfer worker stopped unexpectedly")]
    private partial void LogWorkerStopped(Exception exception);
}

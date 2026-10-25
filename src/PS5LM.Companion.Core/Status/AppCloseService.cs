using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Asks the running PS5LM app to quit by writing the <c>quit</c> command file both apps poll. They then leave through
/// the system's exit path; the companion never kills the title. A request nobody picks up is removed again, so a
/// stale file cannot end the next launch.
/// </summary>
public sealed class AppCloseService(
    IPS5FileSystem fileSystem,
    CompanionStatusService status,
    CompanionStatusStore store,
    LoadTracker loadTracker,
    TimeSpan pickupWait,
    TimeSpan pollInterval)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OperationResult> CloseAsync(PS5Variant variant, CancellationToken cancellationToken)
    {
        if (VariantProfile.For(variant) is not { } profile)
        {
            return new OperationResult(OperationOutcome.Invalid, "Choose PS5LM or PS5LM Plus.");
        }

        var state = store.Current.AppControl?.Apps.FirstOrDefault(a => a.Variant == variant)?.State;
        if (state == AppRunState.NotRunning)
        {
            return new OperationResult(OperationOutcome.NoChange, $"{profile.DisplayName} is not running.");
        }

        if (state == AppRunState.RunningVariantUnknown)
        {
            // The quit file is shared by both apps, so it would end whichever one runs.
            return new OperationResult(OperationOutcome.Conflict, "A PS5LM app is running, but the console does not say which one, so Close is not offered for either.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new OperationResult(OperationOutcome.Conflict, "A close request is already in progress.");
        }

        try
        {
            if (status.LastInstallation is null)
            {
                await status.RefreshAsync(cancellationToken);
            }

            if (status.LastInstallation?.StateRoot is not { } root)
            {
                return new OperationResult(OperationOutcome.Unavailable, status.LastInstallation?.Message ?? "The console's state folder is not reachable.");
            }

            var path = $"{root.FtpPath}/{VariantProfile.QuitCommandFile}";
            if (await fileSystem.FileExistsAsync(path, cancellationToken)
                || await fileSystem.FileExistsAsync($"{root.FtpPath}/{VariantProfile.LoadCommandFile}", cancellationToken)
                || await fileSystem.FileExistsAsync($"{root.FtpPath}/{VariantProfile.UnloadCommandFile}", cancellationToken))
            {
                return new OperationResult(OperationOutcome.Conflict, "A previous command has not been picked up yet.");
            }

            await fileSystem.WriteTextAsync(path, "quit\n", cancellationToken);

            var taken = false;
            try
            {
                var deadline = DateTimeOffset.UtcNow + pickupWait;
                while (true)
                {
                    await Task.Delay(pollInterval, cancellationToken);
                    if (!await fileSystem.FileExistsAsync(path, cancellationToken))
                    {
                        taken = true;
                        break;
                    }

                    if (DateTimeOffset.UtcNow >= deadline)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (!taken)
                {
                    // Also on cancellation or an FTP failure: a stale quit file would end the next launch.
                    await WithdrawAsync(path);
                }
            }

            if (!taken)
            {
                return new OperationResult(OperationOutcome.Unavailable, $"{profile.DisplayName} did not take the quit request (is it running, and not busy loading?). The request was withdrawn.");
            }

            loadTracker.Clear();
            store.Publish(await status.RefreshAsync(cancellationToken));
            return new OperationResult(OperationOutcome.Accepted, $"{profile.DisplayName} took the quit request and is closing.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task WithdrawAsync(string path)
    {
        // Independent of the caller's token, but bounded.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await fileSystem.DeleteAsync(path, cleanup.Token);
        }
        catch (Exception ex) when (ex is ConsoleUnreachableException or OperationCanceledException)
        {
        }
    }
}

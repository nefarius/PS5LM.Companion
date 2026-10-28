using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Brings a stuck Payload Manager back remotely: ask it to exit, kill it through VoidShell when it will not,
/// then start the pinned, hash-verified copy through elfldr and wait until it answers. Only one restart runs per console.
/// </summary>
public sealed class PayloadManagerRestartService(
    PS5Options options,
    PayloadManagerRecovery? recovery,
    IElfldrProbe elfldr,
    CompanionStatusService status,
    CompanionStatusStore store,
    TimeProvider time)
{
    private const string ProcessPrefix = "pldmgr";

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Pause between checks while waiting. Tests use a few milliseconds.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Replaces every wait timeout; tests use a few milliseconds.</summary>
    public TimeSpan? WaitOverride { get; init; }

    public async Task<OperationResult> RestartAsync(CancellationToken cancellationToken)
    {
        if (recovery is null)
        {
            return new OperationResult(OperationOutcome.Unavailable, "This console has no restart support configured.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new OperationResult(OperationOutcome.Conflict, "A Payload Manager restart is already in progress.");
        }

        try
        {
            if (!await elfldr.IsListeningAsync(cancellationToken))
            {
                return new OperationResult(OperationOutcome.Unavailable, "elfldr is not listening, so a new Payload Manager cannot be started. " + PayloadDependencies.ElfldrBootstrap(options.ElfldrPort));
            }

            // Stopping the old manager cannot be undone remotely, so prove elfldr takes a connection first.
            if (!await recovery.Elfldr.AcceptsAsync(cancellationToken))
            {
                return new OperationResult(
                    OperationOutcome.Unavailable,
                    "elfldr accepts connections but closes them at once, so it could not start a new Payload Manager. Nothing was stopped. Reboot the console to reset elfldr.");
            }

            var port = options.PayloadManagerBaseAddress().Port;
            var stopped = await StopAsync(recovery, port, cancellationToken);
            if (stopped is not null)
            {
                return stopped;
            }

            string path;
            try
            {
                path = await recovery.Artifacts.FetchAsync(recovery.Pinned.Url, recovery.Pinned.Sha256, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                return new OperationResult(OperationOutcome.Unavailable, $"The old Payload Manager is stopped, but {recovery.Pinned.FileName} could not be fetched: {ex.Message}");
            }

            try
            {
                await recovery.Elfldr.SendAsync(path, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
            {
                return new OperationResult(OperationOutcome.Unavailable, $"The old Payload Manager is stopped, but sending the new one to elfldr failed: {ex.Message} Reboot the console so autoload starts Payload Manager again.");
            }

            var answered = await WaitAsync(async ct => await recovery.Manager.GetVersionAsync(ct) is not null, 60, cancellationToken);
            store.Publish(await status.RefreshAsync(cancellationToken));
            return answered
                ? new OperationResult(OperationOutcome.Completed, $"Payload Manager {recovery.Pinned.Version} was restarted and answers again.")
                : new OperationResult(OperationOutcome.Unavailable, "The new Payload Manager was sent to elfldr but did not answer within 60 s.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns <see langword="null"/> once the manager's port is closed, otherwise the failure.</summary>
    private async Task<OperationResult?> StopAsync(PayloadManagerRecovery recovery, int port, CancellationToken cancellationToken)
    {
        if (!await recovery.Probe.PortOpenAsync(port, cancellationToken))
        {
            return null;
        }

        // A manager that still serves requests exits on its own; a stuck one never answers, so the wait is short.
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(WaitOverride ?? TimeSpan.FromSeconds(5));
            try
            {
                await recovery.Manager.ShutdownAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Expected for a stuck manager; the port check below decides.
            }
        }

        if (await WaitAsync(async ct => !await recovery.Probe.PortOpenAsync(port, ct), 5, cancellationToken))
        {
            return null;
        }

        var processes = recovery.Processes is null ? null : await recovery.Processes.ListAsync(cancellationToken);
        if (processes is null)
        {
            return new OperationResult(
                OperationOutcome.Unavailable,
                "Payload Manager does not exit and VoidShell cannot stop it. Run `update` to install the VoidShell release with process control, or reboot the console.");
        }

        var managers = processes.Where(p => p.Name.StartsWith(ProcessPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (managers.Count == 0)
        {
            return new OperationResult(OperationOutcome.Unavailable, $"Port {port} is open but no process named {ProcessPrefix}* runs, so something else holds it. Reboot the console.");
        }

        foreach (var process in managers)
        {
            await recovery.Processes!.KillAsync(process.Pid, cancellationToken);
        }

        return await WaitAsync(async ct => !await recovery.Probe.PortOpenAsync(port, ct), 10, cancellationToken)
            ? null
            : new OperationResult(OperationOutcome.Unavailable, $"Payload Manager was killed but port {port} is still open. Reboot the console.");
    }

    private async Task<bool> WaitAsync(Func<CancellationToken, Task<bool>> check, int seconds, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + (WaitOverride ?? TimeSpan.FromSeconds(seconds));
        while (true)
        {
            if (await check(cancellationToken))
            {
                return true;
            }

            if (time.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(WaitOverride ?? PollInterval, time, cancellationToken);
        }
    }
}

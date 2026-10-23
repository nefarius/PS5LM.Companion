using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Launches one installed payload through Payload Manager. A second request while one is running is refused,
/// and the status store is refreshed after a transfer so the dashboard does not wait for the next poll.
/// </summary>
public sealed class DependencyLaunchService(
    PS5Options options,
    IPayloadCatalog catalog,
    IPayloadLauncher launcher,
    IElfldrProbe elfldr,
    CompanionStatusService status,
    CompanionStatusStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OperationResult> LaunchAsync(string? id, CancellationToken cancellationToken)
    {
        if (string.Equals(id, PayloadDependencies.ManagerId, StringComparison.OrdinalIgnoreCase))
        {
            return new OperationResult(OperationOutcome.Invalid, "Payload Manager cannot launch itself.");
        }

        if (string.Equals(id, PayloadDependencies.Exporter, StringComparison.OrdinalIgnoreCase))
        {
            return new OperationResult(OperationOutcome.Invalid, "ps5-exporter is bundled with PS5LM Plus and is not launched separately.");
        }

        if (string.Equals(id, PayloadDependencies.Elfldr, StringComparison.OrdinalIgnoreCase))
        {
            return new OperationResult(OperationOutcome.Invalid, PayloadDependencies.ElfldrBootstrap(options.ElfldrPort));
        }

        if (!PayloadDependencies.TryGetLaunchable(id, out var name))
        {
            return new OperationResult(OperationOutcome.NotFound, "That payload is not one of the dependencies the companion can launch.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new OperationResult(OperationOutcome.Conflict, "A payload launch is already in progress.");
        }

        try
        {
            var snapshot = await catalog.ReadAsync(cancellationToken);
            if (!snapshot.Available)
            {
                return new OperationResult(OperationOutcome.Unavailable, snapshot.Message ?? "Payload Manager is not reachable.");
            }

            var payload = snapshot.Installed.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (payload is null)
            {
                return new OperationResult(OperationOutcome.NotFound, $"{name} is not installed in Payload Manager.");
            }

            if (payload.Id.Equals(PayloadDependencies.ManagerId, StringComparison.OrdinalIgnoreCase))
            {
                return new OperationResult(OperationOutcome.Invalid, "Payload Manager cannot launch itself.");
            }

            if (!PayloadDependencies.IsSafeSlot(payload.Slot))
            {
                return new OperationResult(OperationOutcome.Invalid, "The installed payload slot is not valid.");
            }

            if (!await elfldr.IsListeningAsync(cancellationToken))
            {
                return new OperationResult(OperationOutcome.Unavailable, PayloadDependencies.ElfldrBootstrap(options.ElfldrPort));
            }

            var launched = await launcher.LaunchAsync(payload.Slot, cancellationToken);
            if (!launched.Accepted)
            {
                var outcome = launched.StatusCode switch
                {
                    404 => OperationOutcome.NotFound,
                    400 => OperationOutcome.Invalid,
                    _ => OperationOutcome.Unavailable,
                };
                return new OperationResult(outcome, launched.Message);
            }

            store.Publish(await status.RefreshAsync(cancellationToken));
            return new OperationResult(
                OperationOutcome.Accepted,
                $"Payload Manager transferred {name} to elfldr. That confirms the hand-off, not that the payload finished starting.");
        }
        finally
        {
            _gate.Release();
        }
    }
}

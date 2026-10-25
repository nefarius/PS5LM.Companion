using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Core.Models;

/// <summary>
/// Deletes one model file on one console. An active transfer of that same file blocks deletion until it finishes
/// or is cancelled; transfers to another console or another file do not.
/// </summary>
public sealed class ModelDeletionService(TransferService transfers)
{
    public Task<OperationResult> DeleteAsync(ConsoleRuntime console, string? appPath, CancellationToken cancellationToken) =>
        console.Control.DeleteAsync(appPath, model => IsTransferInProgress(console.Id, model), cancellationToken);

    /// <summary>Whether this console is currently moving <paramref name="model"/> onto its storage root.</summary>
    public bool IsTransferInProgress(string consoleId, ModelFile model) =>
        transfers.Snapshot(consoleId).Any(transfer =>
            transfer.IsActive
            && transfer.FileName.Equals(model.FileName, StringComparison.Ordinal)
            && transfer.TargetRoot.Equals(model.Location, StringComparison.OrdinalIgnoreCase));
}

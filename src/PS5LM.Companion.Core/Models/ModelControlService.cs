using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.Core.Models;

public enum OperationOutcome
{
    /// <summary>The command was written; the app picks it up within about a second and restarts itself.</summary>
    Accepted = 0,

    /// <summary>Nothing to do: the requested state already holds.</summary>
    NoChange = 1,

    NotFound = 2,

    /// <summary>Another command is still waiting to be consumed.</summary>
    Conflict = 3,

    /// <summary>The console or the app's state folder cannot be reached right now.</summary>
    Unavailable = 4,

    Invalid = 5,
}

public sealed record OperationResult(OperationOutcome Outcome, string? Message = null)
{
    public bool Succeeded => Outcome is OperationOutcome.Accepted or OperationOutcome.NoChange;
}

/// <summary>
/// Loads and unloads models through the command files both apps already poll once a second:
/// <c>load</c> holds the model path as the app sees it, <c>unload</c> holds <c>-</c> (an empty file would be ignored).
/// </summary>
public sealed class ModelControlService(
    IPS5FileSystem fileSystem,
    ModelCatalog catalog,
    CompanionStatusService statusService,
    CompanionStatusStore store,
    LoadTracker loadTracker)
{
    // The check for a pending command and the write must not interleave between concurrent requests.
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    public async Task<OperationResult> LoadAsync(string? appPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appPath))
        {
            return new OperationResult(OperationOutcome.Invalid, "A model path is required.");
        }

        var installation = await InstallationAsync(cancellationToken);
        if (installation?.StateRoot is not { } root)
        {
            return Unavailable(installation);
        }

        // Only paths that really are a model on the console may reach the command file.
        var models = await catalog.ListAsync(installation, store.Current.LoadedModel, cancellationToken);
        var model = models.FirstOrDefault(m => !m.IsProjector && m.AppPath.Equals(appPath, StringComparison.Ordinal));
        if (model is null)
        {
            return new OperationResult(OperationOutcome.NotFound, "That model is not on the console.");
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            // A pending command (say an unload) may still change what is loaded, so it outranks the no-op.
            if (await CommandPendingAsync(root, cancellationToken))
            {
                return new OperationResult(OperationOutcome.Conflict, "A previous command has not been picked up yet.");
            }

            if (model.Loaded)
            {
                return new OperationResult(OperationOutcome.NoChange, "That model is already loaded.");
            }

            await fileSystem.WriteTextAsync($"{root.FtpPath}/{VariantProfile.LoadCommandFile}", model.AppPath + "\n", cancellationToken);
            loadTracker.MarkRequested(model.AppPath);
            return new OperationResult(OperationOutcome.Accepted, "Load requested; the app restarts to give the model all memory.");
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public async Task<OperationResult> UnloadAsync(CancellationToken cancellationToken)
    {
        var installation = await InstallationAsync(cancellationToken);
        if (installation?.StateRoot is not { } root)
        {
            return Unavailable(installation);
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            if (await CommandPendingAsync(root, cancellationToken))
            {
                return new OperationResult(OperationOutcome.Conflict, "A previous command has not been picked up yet.");
            }

            await fileSystem.WriteTextAsync($"{root.FtpPath}/{VariantProfile.UnloadCommandFile}", "-\n", cancellationToken);
            loadTracker.Clear();
            return new OperationResult(OperationOutcome.Accepted, "Unload requested; the app restarts without a model.");
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<Installation?> InstallationAsync(CancellationToken cancellationToken)
    {
        if (statusService.LastInstallation is null)
        {
            await statusService.RefreshAsync(cancellationToken);
        }

        return statusService.LastInstallation;
    }

    private async Task<bool> CommandPendingAsync(Domain.StorageRoot root, CancellationToken cancellationToken) =>
        await fileSystem.FileExistsAsync($"{root.FtpPath}/{VariantProfile.LoadCommandFile}", cancellationToken)
        || await fileSystem.FileExistsAsync($"{root.FtpPath}/{VariantProfile.UnloadCommandFile}", cancellationToken);

    private static OperationResult Unavailable(Installation? installation) =>
        new(OperationOutcome.Unavailable, installation?.Message ?? "The console is not reachable.");
}

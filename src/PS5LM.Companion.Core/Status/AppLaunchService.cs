using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Launches PS5LM or PS5LM Plus through the required VoidShell fork, or through websrv when that fork does not
/// answer. A VoidShell answer that omits the title is final. Only a title the chosen service lists as installed is
/// launched, and a second request while one runs is refused.
/// </summary>
public sealed class AppLaunchService(
    IReadOnlyList<IAppControlProvider> providers,
    CompanionStatusService status,
    CompanionStatusStore store)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<OperationResult> LaunchAsync(PS5Variant variant, CancellationToken cancellationToken)
    {
        if (VariantProfile.For(variant) is not { } profile)
        {
            return new OperationResult(OperationOutcome.Invalid, "Choose PS5LM or PS5LM Plus.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return new OperationResult(OperationOutcome.Conflict, "An app launch is already in progress.");
        }

        try
        {
            var reachable = false;
            foreach (var provider in providers.OrderBy(p => p.Kind))
            {
                var snapshot = await provider.ProbeAsync(cancellationToken);
                if (!snapshot.Available)
                {
                    continue;
                }

                reachable = true;
                if (!snapshot.InstalledTitleIds.Contains(profile.TitleId, StringComparer.OrdinalIgnoreCase))
                {
                    if (provider.Kind == AppProviderKind.VoidShell)
                    {
                        return new OperationResult(OperationOutcome.NotFound, $"{profile.DisplayName} ({profile.TitleId}) is not installed on the console.");
                    }

                    continue;
                }

                var result = await provider.LaunchAsync(profile.TitleId, cancellationToken);
                if (!result.Dispatched)
                {
                    reachable = false;
                    continue;
                }

                if (!result.Accepted)
                {
                    return new OperationResult(OperationOutcome.Unavailable, $"{ProviderLabel(provider.Kind)} could not launch {profile.DisplayName}: {result.Message}");
                }

                store.Publish(await status.RefreshAsync(cancellationToken));
                return new OperationResult(
                    OperationOutcome.Accepted,
                    $"{ProviderLabel(provider.Kind)} accepted the launch of {profile.DisplayName} ({profile.TitleId}). {result.Message}".TrimEnd());
            }

            return reachable
                ? new OperationResult(OperationOutcome.NotFound, $"{profile.DisplayName} ({profile.TitleId}) is not installed on the console.")
                : new OperationResult(
                    OperationOutcome.Unavailable,
                    providers.Any(provider => provider.Kind == AppProviderKind.Websrv)
                        ? "The fixed VoidShell fork is not reachable, and websrv (legacy fallback) is not reachable either."
                        : "The fixed VoidShell fork is not reachable.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ProviderLabel(AppProviderKind kind) => kind switch
    {
        AppProviderKind.Websrv => "websrv (legacy fallback)",
        _ => kind.ToString(),
    };
}

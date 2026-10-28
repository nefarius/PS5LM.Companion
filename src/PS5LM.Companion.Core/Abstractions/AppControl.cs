using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Abstractions;

/// <summary>The services that can list and launch the console's titles.</summary>
public enum AppProviderKind
{
    /// <summary>VoidShell: title library, foreground title and launching on port 7007.</summary>
    VoidShell = 0,

    /// <summary>ps5-payload-dev websrv: title launching on port 8080; no foreground-title API.</summary>
    Websrv = 1,
}

/// <summary>What one app-control service reported on one probe.</summary>
/// <param name="Kind">Which service.</param>
/// <param name="Available">Whether it answered.</param>
/// <param name="Version">Version text, when reported.</param>
/// <param name="InstalledTitleIds">Title IDs it lists as installed.</param>
/// <param name="RunningKnown">Whether <paramref name="ActiveTitleId"/> is meaningful (null then means nothing runs).</param>
/// <param name="ActiveTitleId">The running (or, for upstream VoidShell, foreground) title as the service reported it, possibly truncated.</param>
/// <param name="Message">Why it is unavailable, when it is.</param>
/// <param name="Firmware">The console's system software version (e.g. <c>13.40</c>), when the service reports it.</param>
public sealed record AppProviderSnapshot(
    AppProviderKind Kind,
    bool Available,
    string? Version,
    IReadOnlyList<string> InstalledTitleIds,
    bool RunningKnown,
    string? ActiveTitleId,
    string? Message,
    string? Firmware = null)
{
    public static AppProviderSnapshot Unavailable(AppProviderKind kind, string? message = null) =>
        new(kind, false, null, [], false, null, message);
}

/// <summary>Outcome of one launch request to a service.</summary>
/// <param name="Dispatched">Whether the request reached the service. False lets the caller try another one.</param>
/// <param name="Accepted">Whether the service accepted it. For VoidShell this only means the title was queued.</param>
public sealed record AppLaunchResult(bool Dispatched, bool Accepted, string Message);

/// <summary>Lists and launches titles through VoidShell or websrv.</summary>
public interface IAppControlProvider
{
    AppProviderKind Kind { get; }

    /// <summary>Never throws for an unreachable service: that is an unavailable snapshot.</summary>
    Task<AppProviderSnapshot> ProbeAsync(CancellationToken cancellationToken);

    Task<AppLaunchResult> LaunchAsync(string titleId, CancellationToken cancellationToken);
}

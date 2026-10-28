using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Abstractions;

/// <summary>The services that can list and launch the console's titles.</summary>
public enum AppProviderKind
{
    /// <summary>Required nefarius VoidShell fork: title library, running title, firmware and launching. Setup uses port 7007.</summary>
    VoidShell = 0,

    /// <summary>Legacy ps5-payload-dev websrv fallback: title launching on port 8080. It cannot report the running title.</summary>
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
/// <param name="SemVer">The fork's full SemVer (e.g. <c>1.1.1-nefarius.4</c>); null on upstream builds, which only report <c>V1.1</c>.</param>
public sealed record AppProviderSnapshot(
    AppProviderKind Kind,
    bool Available,
    string? Version,
    IReadOnlyList<string> InstalledTitleIds,
    bool RunningKnown,
    string? ActiveTitleId,
    string? Message,
    string? Firmware = null,
    string? SemVer = null)
{
    public static AppProviderSnapshot Unavailable(AppProviderKind kind, string? message = null) =>
        new(kind, false, null, [], false, null, message);
}

/// <summary>Outcome of one launch request to a service.</summary>
/// <param name="Dispatched">Whether the request reached the service. False lets the caller try another one.</param>
/// <param name="Accepted">Whether the service accepted it. For VoidShell this only means the title was queued.</param>
public sealed record AppLaunchResult(bool Dispatched, bool Accepted, string Message);

/// <summary>Lists and launches titles through the required VoidShell fork, or websrv when that fork does not answer.</summary>
public interface IAppControlProvider
{
    AppProviderKind Kind { get; }

    /// <summary>Never throws for an unreachable service: that is an unavailable snapshot.</summary>
    Task<AppProviderSnapshot> ProbeAsync(CancellationToken cancellationToken);

    Task<AppLaunchResult> LaunchAsync(string titleId, CancellationToken cancellationToken);
}

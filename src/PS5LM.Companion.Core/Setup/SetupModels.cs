namespace PS5LM.Companion.Core.Setup;

/// <summary>What the person asked setup to do.</summary>
/// <param name="Host">Console IP or host name.</param>
/// <param name="AppId">Manifest app id to install (<c>ps5lm</c> or <c>ps5lmplus</c>); null with <see cref="SkipApp"/>.</param>
public sealed record SetupOptions(string Host, string? AppId)
{
    public bool UpgradePayloadManager { get; init; }

    /// <summary>Re-upload payloads and extra files and reinstall the app even when they look current.</summary>
    public bool Force { get; init; }

    public bool SkipApp { get; init; }

    public int FtpPort { get; init; } = 2121;

    public int ElfldrPort { get; init; } = 9021;

    public int PayloadManagerPort { get; init; } = 8084;

    public int VoidShellPort { get; init; } = 7007;

    /// <summary>Pause between checks while waiting for something to come up.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait for the app to be installed by ShadowMountPlus.</summary>
    public TimeSpan AppInstallTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Replaces every wait timeout and pause; tests use a few milliseconds.</summary>
    public TimeSpan? WaitOverride { get; init; }

    public TimeSpan Wait(TimeSpan normal) => WaitOverride ?? normal;
}

public enum SetupStatus
{
    Ok,
    Warn,
    Fail,
}

/// <summary>One line of the final report.</summary>
public sealed record ReportLine(string Component, SetupStatus Status, string Message);

/// <summary>The outcome of a setup run. Exit code 0 means nothing failed; warnings do not fail it.</summary>
public sealed record SetupReport(IReadOnlyList<ReportLine> Lines)
{
    public int ExitCode => Lines.Any(line => line.Status == SetupStatus.Fail) ? 2 : 0;
}

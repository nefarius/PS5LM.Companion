namespace PS5LM.Companion.Core.Setup;

/// <summary>What the person asked <c>update</c> to do.</summary>
/// <param name="Host">Console IP or host name.</param>
/// <param name="AppId">Limits the app update to one manifest app id; null means every installed variant.</param>
public sealed record UpdateOptions(string Host, string? AppId = null)
{
    /// <summary>Also update the installed PS5LM / PS5LM Plus app.</summary>
    public bool IncludeApps { get; init; }

    /// <summary>Report what is out of date and change nothing.</summary>
    public bool CheckOnly { get; init; }

    public bool UpgradePayloadManager { get; init; }

    public int FtpPort { get; init; } = 2121;

    public int ElfldrPort { get; init; } = 9021;

    public int PayloadManagerPort { get; init; } = 8084;

    public int VoidShellPort { get; init; } = 7007;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Replaces every wait timeout and pause; tests use a few milliseconds.</summary>
    public TimeSpan? WaitOverride { get; init; }

    internal SetupOptions ToSetupOptions() => new(Host, AppId)
    {
        UpgradePayloadManager = UpgradePayloadManager,
        SkipApp = true,
        FtpPort = FtpPort,
        ElfldrPort = ElfldrPort,
        PayloadManagerPort = PayloadManagerPort,
        VoidShellPort = VoidShellPort,
        PollInterval = PollInterval,
        WaitOverride = WaitOverride,
    };
}

/// <summary>
/// The outcome of an update run. Exit code 2 when anything failed, 3 when <c>--check</c> found updates, else 0.
/// </summary>
public sealed record UpdateReport(IReadOnlyList<ReportLine> Lines, bool CheckOnly, int Pending)
{
    public int ExitCode =>
        Lines.Any(line => line.Status == SetupStatus.Fail) ? 2
        : CheckOnly && Pending > 0 ? 3
        : 0;
}

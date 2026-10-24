namespace PS5LM.Companion.Core.Domain;

/// <summary>One payload currently installed in Payload Manager.</summary>
/// <param name="Id">Stable id derived from the catalog name, for example <c>etahen</c>.</param>
/// <param name="Slot">Launch identity, for example <c>repo/etahen@0123abcd</c>.</param>
/// <param name="Name">Display name from Payload Manager. The companion launches by <see cref="Slot"/>.</param>
public sealed record InstalledPayload(string Id, string Slot, string Name);

/// <summary>What Payload Manager reported. A down manager is a normal result, not an exception.</summary>
/// <param name="Available">The installed-payload list was read.</param>
/// <param name="Checked">A read was attempted. The initial status is unchecked.</param>
/// <param name="Message">Why the manager could not be read, when <see cref="Available"/> is false.</param>
/// <param name="Installed">Installed slots. Empty when the manager did not answer.</param>
/// <param name="LaunchRecordedSlots">Slots whose file Payload Manager successfully handed to elfldr.</param>
/// <param name="RunningProcesses">Process names from Payload Manager's process list, or null when the list could not be read.</param>
public sealed record PayloadCatalogSnapshot(
    bool Available,
    bool Checked,
    string? Message,
    IReadOnlyList<InstalledPayload> Installed,
    IReadOnlySet<string> LaunchRecordedSlots,
    IReadOnlySet<string>? RunningProcesses = null)
{
    private static readonly IReadOnlySet<string> NoSlots = new HashSet<string>();

    /// <summary>Used before the first status check.</summary>
    public static PayloadCatalogSnapshot NotChecked { get; } = new(false, false, null, [], NoSlots);

    /// <summary>The manager was asked and did not answer.</summary>
    public static PayloadCatalogSnapshot Unreachable(string message) => new(false, true, message, [], NoSlots);
}

/// <summary>The outcome of asking Payload Manager to send a payload to elfldr.</summary>
/// <param name="Accepted">True when the manager answered that the file was transferred.</param>
/// <param name="StatusCode">The HTTP status, when the manager answered.</param>
/// <param name="Message">Manager or transport detail.</param>
public sealed record PayloadLaunchResult(bool Accepted, int? StatusCode, string Message);

/// <summary>
/// The PS5LM Plus payloads the companion watches. Ids match Payload Manager's catalog slugs,
/// and <c>ps5-exporter</c> is observed through its port because the app bundles it.
/// </summary>
public static class PayloadDependencies
{
    public const string KstuffLite = "kstuff-lite";
    public const string EtaHen = "etahen";
    public const string OnionHen = "onionhen";
    public const string ShadowMountPlus = "shadowmountplus";
    public const string Elfldr = "elfldr";
    public const string FtpSrv = "ftpsrv";
    public const string Exporter = "ps5-exporter";
    public const string ManagerId = "pldmgr";
    public const int DefaultElfldrPort = 9021;
    public const int DefaultPayloadManagerPort = 8084;

    private static readonly DependencyDefinition[] Definitions =
    [
        new(KstuffLite, "kstuff-lite", DependencyRequirement.Required, Verification.Process, true, ["kstuff.elf", "kstuff-lite.elf"]),
        new(EtaHen, "etaHEN or onionHEN", DependencyRequirement.Required, Verification.Process, true, ["etahen.elf", "daemon.elf", "util.elf", "onion_daemon.elf", "onion_util.elf"], [OnionHen]),
        new(ShadowMountPlus, "ShadowMountPlus", DependencyRequirement.Required, Verification.Process, true, ["shadowmountplus.elf"]),
        new(Elfldr, "elfldr", DependencyRequirement.Recommended, Verification.ElfldrPort, false, []),
        new(FtpSrv, "ftpsrv", DependencyRequirement.Recommended, Verification.Ftp, true, []),
        new(Exporter, "ps5-exporter", DependencyRequirement.Bundled, Verification.Exporter, false, []),
    ];

    /// <summary>Turns a catalog read and the port checks into the rows the dashboard shows.</summary>
    public static IReadOnlyList<DependencyStatus> Evaluate(
        PayloadCatalogSnapshot catalog,
        bool ftpReachable,
        bool exporterReachable,
        bool elfldrListening,
        int elfldrPort)
    {
        return Definitions.Select(definition => EvaluateOne(definition, catalog, ftpReachable, exporterReachable, elfldrListening, elfldrPort)).ToList();
    }

    /// <summary>The display name of a payload the companion is willing to launch, or false for unknown, bundled and elfldr ids.</summary>
    public static bool TryGetLaunchable(string? id, out string name)
    {
        var definition = Definitions.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (definition is not { Launchable: true })
        {
            name = string.Empty;
            return false;
        }

        name = definition.Name;
        return true;
    }

    /// <summary>True when a Payload Manager payload id satisfies the dependency, for example onionHEN for etaHEN.</summary>
    public static bool Satisfies(string dependencyId, string installedId)
    {
        var definition = Definitions.FirstOrDefault(item => item.Id.Equals(dependencyId, StringComparison.OrdinalIgnoreCase));
        return definition is not null && definition.Accepts(installedId);
    }

    /// <summary>Why elfldr has to be loaded before Payload Manager can launch anything.</summary>
    public static string ElfldrBootstrap(int port) =>
        $"elfldr is not listening on port {port}. Payload Manager sends every payload through elfldr and cannot start elfldr itself. Load elfldr another way, then launch the other payloads.";

    private static DependencyStatus EvaluateOne(
        DependencyDefinition definition,
        PayloadCatalogSnapshot catalog,
        bool ftpReachable,
        bool exporterReachable,
        bool elfldrListening,
        int elfldrPort)
    {
        var match = catalog.Installed.FirstOrDefault(payload =>
            definition.Accepts(payload.Id)
            && catalog.LaunchRecordedSlots.Contains(payload.Slot))
            ?? catalog.Installed.FirstOrDefault(payload => definition.Accepts(payload.Id));
        var runningName = catalog.RunningProcesses is null
            ? null
            : definition.ProcessNames.FirstOrDefault(catalog.RunningProcesses.Contains);
        var running = runningName is not null;
        var installed = match is not null;
        var recorded = match is not null && catalog.LaunchRecordedSlots.Contains(match.Slot);
        var verified = definition.VerificationKind switch
        {
            Verification.Ftp => ftpReachable,
            Verification.Exporter => exporterReachable,
            Verification.ElfldrPort => elfldrListening,
            _ => false,
        };

        var presence = definition.VerificationKind == Verification.Exporter
            ? DependencyPresence.Bundled
            : !catalog.Available
                ? DependencyPresence.Unknown
                : installed || running || verified ? DependencyPresence.Installed : DependencyPresence.NotInstalled;

        var (health, message) = Describe(definition, catalog, installed, recorded, verified, runningName, elfldrPort);
        var canLaunch = definition.Launchable
            && catalog.Available
            && match is not null
            && IsSafeSlot(match.Slot)
            && !match.Id.Equals(ManagerId, StringComparison.OrdinalIgnoreCase);

        return new DependencyStatus(definition.Id, definition.Name, definition.Requirement, presence, health, canLaunch, message);
    }

    private static (DependencyHealth Health, string Message) Describe(
        DependencyDefinition definition,
        PayloadCatalogSnapshot catalog,
        bool installed,
        bool recorded,
        bool verified,
        string? runningName,
        int elfldrPort)
    {
        if (!catalog.Checked)
        {
            return (DependencyHealth.Unavailable, "Not checked yet.");
        }

        if (verified)
        {
            return definition.VerificationKind switch
            {
                Verification.Ftp => (DependencyHealth.Verified, installed
                    ? "ftpsrv is answering."
                    : "ftpsrv is answering. It was loaded outside Payload Manager."),
                Verification.Exporter => (DependencyHealth.Verified, "ps5-exporter is answering. PS5LM Plus bundles it, so it is not launched separately."),
                _ => (DependencyHealth.Verified, "elfldr is listening, so Payload Manager can launch other payloads."),
            };
        }

        if (definition.VerificationKind == Verification.ElfldrPort)
        {
            return (DependencyHealth.Unknown, ElfldrBootstrap(elfldrPort));
        }

        if (definition.VerificationKind == Verification.Ftp)
        {
            return (DependencyHealth.Unknown, installed
                ? "ftpsrv is installed, and it is not answering on its port."
                : "ftpsrv is not answering on its port.");
        }

        if (definition.VerificationKind == Verification.Exporter)
        {
            return (DependencyHealth.Unknown, "ps5-exporter is not answering. PS5LM Plus starts its bundled copy through elfldr once the app is running.");
        }

        if (runningName is not null)
        {
            return (DependencyHealth.Verified, installed
                ? $"{definition.Name} is running (process {runningName})."
                : $"{definition.Name} is running (process {runningName}). It was loaded outside Payload Manager.");
        }

        if (!catalog.Available)
        {
            return (DependencyHealth.Unavailable, "Payload Manager is not reachable, so this payload cannot be checked.");
        }

        if (!installed)
        {
            return (DependencyHealth.Unknown, $"{definition.Name} is not installed in Payload Manager.");
        }

        if (catalog.RunningProcesses is not null)
        {
            var names = string.Join(" or ", definition.ProcessNames);
            return (DependencyHealth.NotRunning, recorded
                ? $"Payload Manager recorded a launch, and no process named {names} is running now."
                : $"{definition.Name} is installed, and no process named {names} is running.");
        }

        if (recorded)
        {
            return (DependencyHealth.LaunchRecorded, $"Payload Manager recorded a launch. It has no way to tell whether {definition.Name} is still active.");
        }

        return (DependencyHealth.Unknown, $"{definition.Name} is installed. No launch is recorded, and nothing can tell whether it is active.");
    }

    /// <summary>Slots are path-like ids from Payload Manager. Reject traversal and control characters before they reach a URL.</summary>
    public static bool IsSafeSlot(string? slot) =>
        slot is { Length: > 0 and <= 512 }
        && !slot.Contains('\\', StringComparison.Ordinal)
        && !slot.Contains("..", StringComparison.Ordinal)
        && slot.All(static c => !char.IsControl(c));

    private enum Verification
    {
        Ftp,
        Exporter,
        ElfldrPort,
        Process,
    }

    /// <summary>A payload the companion knows how to describe.</summary>
    private sealed record DependencyDefinition(string Id, string Name, DependencyRequirement Requirement, Verification VerificationKind, bool Launchable, IReadOnlyList<string> ProcessNames, IReadOnlyList<string>? Aliases = null)
    {
        public bool Accepts(string payloadId) =>
            Id.Equals(payloadId, StringComparison.OrdinalIgnoreCase)
            || (Aliases?.Any(alias => alias.Equals(payloadId, StringComparison.OrdinalIgnoreCase)) ?? false);
    }
}

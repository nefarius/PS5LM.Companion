using PS5LM.Companion.Core.Abstractions;

namespace PS5LM.Companion.Core.Setup;

/// <summary>
/// Everything needed to bring a stuck Payload Manager back without touching the console: its own admin
/// calls, a TCP probe for its port, VoidShell's process control, and elfldr plus the pinned release to start a fresh copy.
/// </summary>
public sealed record PayloadManagerRecovery(
    IPayloadManagerAdmin Manager,
    IConsoleProbe Probe,
    IProcessControl? Processes,
    IElfldrSender Elfldr,
    IArtifactStore Artifacts,
    ManifestPayloadManager Pinned);

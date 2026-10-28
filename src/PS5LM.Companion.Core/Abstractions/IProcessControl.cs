namespace PS5LM.Companion.Core.Abstractions;

/// <summary>A process on the console, as the kernel names it (at most 19 characters).</summary>
public sealed record RemoteProcess(int Pid, string Name);

/// <summary>Lists and stops processes on the console. VoidShell provides it from the pinned fork release.</summary>
public interface IProcessControl
{
    /// <summary>The running processes, or <see langword="null"/> when the console cannot say (unreachable or an older VoidShell).</summary>
    Task<IReadOnlyList<RemoteProcess>?> ListAsync(CancellationToken cancellationToken);

    /// <summary>Sends SIGKILL; false when the console refused or did not answer.</summary>
    Task<bool> KillAsync(int pid, CancellationToken cancellationToken);
}

using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Core.Consoles;

public enum ProbeOutcome
{
    Pass = 0,
    Fail = 1,
    Skipped = 2,
}

/// <summary>One reachability check against unsaved console settings.</summary>
public sealed record ProbeCheck(string Name, ProbeOutcome Outcome, string Detail);

/// <summary>The checks from one connection test, in the order they ran.</summary>
public sealed record ConsoleProbeReport(IReadOnlyList<ProbeCheck> Checks);

/// <summary>
/// Tries the services a console needs, using settings that have not been saved.
/// A closed llama-server port is reported as skipped: it stays closed until a model is loaded.
/// </summary>
public interface IConsoleProbe
{
    Task<ConsoleProbeReport> TestAsync(PS5Options options, CancellationToken cancellationToken);
}

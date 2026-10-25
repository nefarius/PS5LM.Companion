namespace PS5LM.Companion.Core.Consoles;

/// <summary>A console found by probing the local network. Hosts that are already configured are left out.</summary>
public sealed record DiscoveredConsole(
    string Host,
    bool Ps5Lm,
    bool Ps5LmPlus,
    bool PayloadManager,
    bool VoidShell,
    bool Exporter);

/// <summary>Looks for consoles on the local IPv4 networks, or on one CIDR entered by hand.</summary>
public interface IConsoleDiscovery
{
    /// <param name="cidr">Null scans the up interfaces. A prefix wider than /22 is capped at /22.</param>
    Task<IReadOnlyList<DiscoveredConsole>> ScanAsync(string? cidr, CancellationToken cancellationToken);
}

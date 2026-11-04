namespace PS5LM.Companion.Core.Consoles;

/// <summary>A console found by probing the local network. Hosts that are already configured are left out.</summary>
/// <param name="Host">The IPv4 address that answered.</param>
/// <param name="Ps5Lm">Whether the PS5LM title folder or VoidShell library entry is present.</param>
/// <param name="Ps5LmPlus">Whether the PS5LM Plus title folder or VoidShell library entry is present.</param>
/// <param name="PayloadManager">Whether port 8084 accepted a connection.</param>
/// <param name="VoidShell">Whether port 7007 accepted a connection, or VoidShell confirmed the console.</param>
/// <param name="Exporter">Whether port 9100 accepted a connection.</param>
/// <param name="HostName">A reverse-DNS name that resolves back to <paramref name="Host"/>, when one exists.</param>
/// <param name="Ftp">Whether FTP confirmed the console. False when only VoidShell did.</param>
/// <param name="VoidShellFork">Whether VoidShell reported a <c>semver</c>, which only the nefarius fork does.</param>
public sealed record DiscoveredConsole(
    string Host,
    bool Ps5Lm,
    bool Ps5LmPlus,
    bool PayloadManager,
    bool VoidShell,
    bool Exporter,
    string? HostName = null,
    bool Ftp = true,
    bool VoidShellFork = false);

/// <summary>
/// Looks for consoles on the local IPv4 networks, or on one CIDR entered by hand.
/// FTP is tried first. VoidShell on port 7007 confirms a console when FTP is closed or the login fails.
/// </summary>
public interface IConsoleDiscovery
{
    /// <param name="cidr">Null scans the up interfaces. A prefix wider than /22 is capped at /22.</param>
    Task<IReadOnlyList<DiscoveredConsole>> ScanAsync(string? cidr, CancellationToken cancellationToken);
}

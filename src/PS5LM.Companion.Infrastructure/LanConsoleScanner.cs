using System.Net.NetworkInformation;
using System.Net.Sockets;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Infrastructure;

/// <summary>
/// Finds consoles by connecting to the FTP port, then confirming the login and the PS5LM folders.
/// A Docker bridge only sees the container subnet, so a CIDR can be given instead of the interfaces.
/// </summary>
public sealed class LanConsoleScanner(IConsoleConnectorFactory factory, IConsoleRegistry registry) : IConsoleDiscovery
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);
    private const int SweepParallelism = 64;
    private const int ConfirmParallelism = 8;

    public async Task<IReadOnlyList<DiscoveredConsole>> ScanAsync(string? cidr, CancellationToken cancellationToken)
    {
        var configured = registry.All.Select(console => console.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hosts = string.IsNullOrWhiteSpace(cidr) ? InterfaceHosts() : ScanRanges.Expand(cidr);
        var candidates = hosts
            .Where(host => !configured.Contains(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var open = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = SweepParallelism,
            CancellationToken = cancellationToken,
        }, async (host, token) =>
        {
            if (await PortOpenAsync(host, 2121, token))
            {
                open.Add(host);
            }
        });

        var found = new System.Collections.Concurrent.ConcurrentBag<DiscoveredConsole>();
        await Parallel.ForEachAsync(open, new ParallelOptions
        {
            MaxDegreeOfParallelism = ConfirmParallelism,
            CancellationToken = cancellationToken,
        }, async (host, token) =>
        {
            if (await ConfirmAsync(host, token) is { } console)
            {
                found.Add(console);
            }
        });

        return found.OrderBy(console => console.Host, StringComparer.Ordinal).ToList();
    }

    private static List<string> InterfaceHosts()
    {
        var hosts = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                hosts.AddRange(ScanRanges.Around(address.Address, address.PrefixLength));
            }
        }

        return hosts;
    }

    private async Task<DiscoveredConsole?> ConfirmAsync(string host, CancellationToken cancellationToken)
    {
        var connectors = factory.Create(host, new PS5Options { Host = host });
        try
        {
            await connectors.FileSystem.ListAsync("/", cancellationToken);
            var ps5lm = await connectors.FileSystem.DirectoryExistsAsync(VariantProfile.PS5LM.InstallPath, cancellationToken);
            var plus = await connectors.FileSystem.DirectoryExistsAsync(VariantProfile.PS5LMPlus.InstallPath, cancellationToken);
            if (!ps5lm && !plus)
            {
                return null;
            }

            return new DiscoveredConsole(
                host,
                ps5lm,
                plus,
                await PortOpenAsync(host, 8084, cancellationToken),
                await PortOpenAsync(host, 7007, cancellationToken),
                await PortOpenAsync(host, 9100, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            (connectors.FileSystem as IDisposable)?.Dispose();
        }
    }

    private static async Task<bool> PortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }
}

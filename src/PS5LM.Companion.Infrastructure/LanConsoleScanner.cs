using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Infrastructure;

/// <summary>
/// Finds consoles by connecting to FTP, then confirming the login and the PS5LM folders.
/// When FTP is closed or that login fails, VoidShell on port 7007 confirms the console instead.
/// A Docker bridge only sees the container subnet, so a CIDR can be given instead of the interfaces.
/// </summary>
public sealed class LanConsoleScanner(IConsoleConnectorFactory factory, IConsoleRegistry registry, IHostNameResolver hostNames) : IConsoleDiscovery
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);
    private const int SweepParallelism = 64;
    private const int ConfirmParallelism = 8;
    private const int FtpPort = 2121;
    private const int VoidShellPort = 7007;
    private const int PayloadManagerPort = 8084;
    private const int ExporterPort = 9100;

    public async Task<IReadOnlyList<DiscoveredConsole>> ScanAsync(string? cidr, CancellationToken cancellationToken)
    {
        var configured = registry.All.Select(console => console.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hosts = string.IsNullOrWhiteSpace(cidr) ? InterfaceHosts() : ScanRanges.Expand(cidr);
        var candidates = hosts
            .Where(host => !configured.Contains(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var open = new ConcurrentBag<OpenHost>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = SweepParallelism,
            CancellationToken = cancellationToken,
        }, async (host, token) =>
        {
            var ftpTask = PortOpenAsync(host, FtpPort, token);
            var voidShellTask = PortOpenAsync(host, VoidShellPort, token);
            await Task.WhenAll(ftpTask, voidShellTask);
            var ftpOpen = await ftpTask;
            var voidShellOpen = await voidShellTask;
            if (ftpOpen || voidShellOpen)
            {
                open.Add(new OpenHost(host, ftpOpen, voidShellOpen));
            }
        });

        var found = new ConcurrentBag<DiscoveredConsole>();
        await Parallel.ForEachAsync(open, new ParallelOptions
        {
            MaxDegreeOfParallelism = ConfirmParallelism,
            CancellationToken = cancellationToken,
        }, async (candidate, token) =>
        {
            // 8084 and 9100 are only worth checking once the host is actually a console.
            if (await ConfirmAsync(candidate.Host, candidate.Ftp, candidate.VoidShell, false, false, configured, token) is not { } console)
            {
                return;
            }

            var payloadManagerTask = PortOpenAsync(candidate.Host, PayloadManagerPort, token);
            var exporterTask = PortOpenAsync(candidate.Host, ExporterPort, token);
            await Task.WhenAll(payloadManagerTask, exporterTask);
            found.Add(console with
            {
                PayloadManager = await payloadManagerTask,
                Exporter = await exporterTask,
            });
        });

        return found.OrderBy(console => console.Host, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// FTP first. A login failure, or a closed FTP port, falls back to VoidShell.
    /// An FTP login that lists no PS5LM folder is not a console and is not asked again over VoidShell.
    /// </summary>
    internal async Task<DiscoveredConsole?> ConfirmAsync(
        string host,
        bool ftpOpen,
        bool voidShellOpen,
        bool payloadManagerOpen,
        bool exporterOpen,
        IReadOnlySet<string> configured,
        CancellationToken cancellationToken)
    {
        var connectors = factory.Create(host, new PS5Options { Host = host });
        try
        {
            var ftp = await TryFtpAsync(connectors, host, ftpOpen, voidShellOpen, payloadManagerOpen, exporterOpen, cancellationToken);
            DiscoveredConsole? found = null;
            if (ftp.Outcome == FtpOutcome.Confirmed && ftp.Console is { } confirmed)
            {
                found = confirmed with
                {
                    VoidShellFork = voidShellOpen && await IsForkAsync(connectors, cancellationToken),
                };
            }
            else if (ftp.Outcome != FtpOutcome.Rejected && voidShellOpen)
            {
                found = await TryVoidShellAsync(connectors, host, payloadManagerOpen, exporterOpen, cancellationToken);
            }

            if (found is null)
            {
                return null;
            }

            var hostName = await hostNames.ResolveAsync(host, cancellationToken);
            if (hostName is not null && configured.Contains(hostName))
            {
                return null;
            }

            return found with { HostName = hostName };
        }
        finally
        {
            (connectors.FileSystem as IDisposable)?.Dispose();
        }
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

    private static async Task<(FtpOutcome Outcome, DiscoveredConsole? Console)> TryFtpAsync(
        ConsoleConnectors connectors,
        string host,
        bool ftpOpen,
        bool voidShellOpen,
        bool payloadManagerOpen,
        bool exporterOpen,
        CancellationToken cancellationToken)
    {
        if (!ftpOpen)
        {
            return (FtpOutcome.Closed, null);
        }

        try
        {
            await connectors.FileSystem.ListAsync("/", cancellationToken);
            var ps5lm = await connectors.FileSystem.DirectoryExistsAsync(VariantProfile.PS5LM.InstallPath, cancellationToken);
            var plus = await connectors.FileSystem.DirectoryExistsAsync(VariantProfile.PS5LMPlus.InstallPath, cancellationToken);
            if (!ps5lm && !plus)
            {
                return (FtpOutcome.Rejected, null);
            }

            return (FtpOutcome.Confirmed, new DiscoveredConsole(
                host,
                ps5lm,
                plus,
                payloadManagerOpen,
                voidShellOpen,
                exporterOpen));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (FtpOutcome.Failed, null);
        }
    }

    private static async Task<DiscoveredConsole?> TryVoidShellAsync(
        ConsoleConnectors connectors,
        string host,
        bool payloadManagerOpen,
        bool exporterOpen,
        CancellationToken cancellationToken)
    {
        var snapshot = await ProbeVoidShellAsync(connectors, cancellationToken);
        if (snapshot is not { Available: true })
        {
            return null;
        }

        return new DiscoveredConsole(
            host,
            HasTitle(snapshot, VariantProfile.PS5LM.TitleId),
            HasTitle(snapshot, VariantProfile.PS5LMPlus.TitleId),
            payloadManagerOpen,
            true,
            exporterOpen,
            Ftp: false,
            VoidShellFork: !string.IsNullOrWhiteSpace(snapshot.SemVer));
    }

    private static async Task<bool> IsForkAsync(ConsoleConnectors connectors, CancellationToken cancellationToken)
    {
        var snapshot = await ProbeVoidShellAsync(connectors, cancellationToken);
        return snapshot is { Available: true } && !string.IsNullOrWhiteSpace(snapshot.SemVer);
    }

    private static async Task<AppProviderSnapshot?> ProbeVoidShellAsync(ConsoleConnectors connectors, CancellationToken cancellationToken)
    {
        var provider = connectors.AppControl?.FirstOrDefault(item => item.Kind == AppProviderKind.VoidShell);
        return provider is null ? null : await provider.ProbeAsync(cancellationToken);
    }

    private static bool HasTitle(AppProviderSnapshot snapshot, string titleId) =>
        snapshot.InstalledTitleIds.Contains(titleId, StringComparer.OrdinalIgnoreCase);

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

    private enum FtpOutcome
    {
        Closed,
        Failed,
        Rejected,
        Confirmed,
    }

    private readonly record struct OpenHost(string Host, bool Ftp, bool VoidShell);
}

using System.Net;

namespace PS5LM.Companion.Infrastructure;

/// <summary>Turns a discovered address into a hostname the companion can connect to, or null.</summary>
public interface IHostNameResolver
{
    Task<string?> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// Reverse DNS with a short timeout. A name is returned only when it is not the address itself and
/// looking it up again includes that address, so the suggestion is a name the companion can reach.
/// </summary>
public sealed class DnsHostNameResolver : IHostNameResolver
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromMilliseconds(1500);

    public async Task<string?> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(host, out var address))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(LookupTimeout);
        try
        {
            // The IPAddress overload cannot be cancelled, so the address is passed as text.
            var entry = await Dns.GetHostEntryAsync(host, timeout.Token);
            var name = Normalise(entry.HostName, host);
            if (name is null)
            {
                return null;
            }

            var resolved = await Dns.GetHostAddressesAsync(name, timeout.Token);
            return resolved.Any(candidate => candidate.Equals(address)) ? name : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Drops an empty name, a trailing root dot, and a name that is just the address.</summary>
    internal static string? Normalise(string? hostName, string address)
    {
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return null;
        }

        var name = hostName.Trim().TrimEnd('.');
        if (name.Length == 0
            || IPAddress.TryParse(name, out _)
            || name.Equals(address, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return name;
    }
}

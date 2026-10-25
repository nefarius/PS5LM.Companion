using System.Net;
using System.Net.Sockets;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>Turns a CIDR, or an interface address, into the hosts worth probing. Nothing wider than /22.</summary>
public static class ScanRanges
{
    public const int WidestPrefix = 22;

    public static IReadOnlyList<string> Expand(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix)
            || prefix is < 0 or > 32)
        {
            throw new FormatException("Enter an IPv4 CIDR, for example 192.168.1.0/24.");
        }

        return Hosts(address, Math.Max(prefix, WidestPrefix));
    }

    public static IReadOnlyList<string> Around(IPAddress address, int prefixLength) =>
        Hosts(address, Math.Clamp(prefixLength, WidestPrefix, 32));

    private static List<string> Hosts(IPAddress address, int prefix)
    {
        var ip = ToUInt(address);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = ip & mask;
        var size = prefix == 32 ? 1u : 1u << (32 - prefix);
        var first = prefix >= 31 ? 0u : 1u;
        var last = prefix >= 31 ? size : size - 1;
        var hosts = new List<string>((int)(last - first));
        for (var offset = first; offset < last; offset++)
        {
            hosts.Add(FromUInt(network + offset));
        }

        return hosts;
    }

    private static uint ToUInt(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string FromUInt(uint value) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}");
}

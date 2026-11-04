using System.Net;

using PS5LM.Companion.Infrastructure;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class DnsHostNameResolverTests
{
    [Theory]
    [InlineData("ps5.lan.", "10.0.0.5", "ps5.lan")]
    [InlineData("  ps5.lan.  ", "10.0.0.5", "ps5.lan")]
    [InlineData("10.0.0.5", "10.0.0.5", null)]
    [InlineData("10.0.0.5.", "10.0.0.5", null)]
    [InlineData("PS5.LAN", "ps5.lan", null)]
    [InlineData(".", "10.0.0.5", null)]
    [InlineData("", "10.0.0.5", null)]
    [InlineData("   ", "10.0.0.5", null)]
    [InlineData(null, "10.0.0.5", null)]
    public void Normalise_keeps_a_name_and_drops_the_address_itself(string? hostName, string address, string? expected) =>
        Assert.Equal(expected, DnsHostNameResolver.Normalise(hostName, address));

    [Fact]
    public async Task A_literal_address_resolves_to_itself()
    {
        var addresses = await new DnsHostNameResolver().AddressesAsync("192.0.2.8", CancellationToken.None);

        var address = Assert.Single(addresses);
        Assert.Equal(IPAddress.Parse("192.0.2.8"), address);
    }

    [Fact]
    public async Task An_invalid_name_resolves_to_no_address()
    {
        var addresses = await new DnsHostNameResolver().AddressesAsync("invalid.", CancellationToken.None);

        Assert.Empty(addresses);
    }
}

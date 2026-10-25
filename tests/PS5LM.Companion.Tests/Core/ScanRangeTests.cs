using System.Net;

using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.Tests.Core;

public sealed class ScanRangeTests
{
    [Fact]
    public void A_slash_24_lists_the_usable_hosts()
    {
        var hosts = ScanRanges.Expand("192.168.1.40/24");

        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[^1]);
        Assert.DoesNotContain("192.168.1.0", hosts);
        Assert.DoesNotContain("192.168.1.255", hosts);
    }

    [Fact]
    public void A_network_wider_than_a_slash_22_is_capped()
    {
        var hosts = ScanRanges.Expand("10.1.5.9/16");

        Assert.Equal(1022, hosts.Count);
        Assert.Equal("10.1.4.1", hosts[0]);
        Assert.Equal("10.1.7.254", hosts[^1]);
    }

    [Fact]
    public void A_single_address_is_one_host() =>
        Assert.Equal(["192.0.2.1"], ScanRanges.Expand("192.0.2.1/32"));

    [Fact]
    public void An_interface_prefix_uses_the_same_cap() =>
        Assert.Equal(1022, ScanRanges.Around(IPAddress.Parse("10.1.5.9"), 16).Count);

    [Theory]
    [InlineData("nope")]
    [InlineData("192.168.1.1/33")]
    [InlineData("2001:db8::/64")]
    public void A_bad_cidr_is_rejected(string cidr) =>
        Assert.Throws<FormatException>(() => ScanRanges.Expand(cidr));
}

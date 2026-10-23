using Microsoft.Extensions.Configuration;

using PS5LM.Companion.App;
using PS5LM.Companion.App.Security;

namespace PS5LM.Companion.Tests.App;

public sealed class ListenAddressGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:8080;http://[::1]:8080")]
    public void Loopback_listeners_need_no_token(string? urls) =>
        ListenAddressGuard.EnsureSafe(urls, new CompanionOptions());

    [Theory]
    [InlineData("http://0.0.0.0:8080")]
    [InlineData("http://+:8080")]
    [InlineData("http://*:8080")]
    [InlineData("http://192.168.1.5:8080")]
    [InlineData("http://localhost:5000;http://0.0.0.0:8080")]
    public void Network_listeners_are_refused_without_a_token(string urls) =>
        Assert.Throws<InvalidOperationException>(() => ListenAddressGuard.EnsureSafe(urls, new CompanionOptions()));

    [Fact]
    public void A_token_or_an_explicit_opt_out_allows_network_listeners()
    {
        ListenAddressGuard.EnsureSafe("http://0.0.0.0:8080", new CompanionOptions { AccessToken = "x" });
        ListenAddressGuard.EnsureSafe("http://0.0.0.0:8080", new CompanionOptions { AllowAnonymous = true });
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void No_listener_setting_means_loopback() =>
        ListenAddressGuard.EnsureSafe(Config(), new CompanionOptions());

    [Theory]
    [InlineData("HTTP_PORTS", "8080")]
    [InlineData("HTTPS_PORTS", "8443")]
    [InlineData("HTTP_PORTS", "8080;9090")]
    [InlineData("Kestrel:Endpoints:Web:Url", "http://0.0.0.0:9000")]
    [InlineData("urls", "http://+:8080")]
    public void Every_listener_source_is_checked(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => ListenAddressGuard.EnsureSafe(Config((key, value)), new CompanionOptions()));

    [Fact]
    public void A_loopback_urls_setting_does_not_hide_a_network_kestrel_endpoint() =>
        Assert.Throws<InvalidOperationException>(() => ListenAddressGuard.EnsureSafe(
            Config(("urls", "http://localhost:5000"), ("Kestrel:Endpoints:Web:Url", "http://0.0.0.0:9000")),
            new CompanionOptions()));

    [Fact]
    public void Loopback_kestrel_endpoints_and_a_token_for_port_only_settings_are_fine()
    {
        ListenAddressGuard.EnsureSafe(Config(("Kestrel:Endpoints:Web:Url", "http://127.0.0.1:9000")), new CompanionOptions());
        ListenAddressGuard.EnsureSafe(Config(("HTTP_PORTS", "8080")), new CompanionOptions { AccessToken = "x" });
    }
}

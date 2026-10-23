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
}

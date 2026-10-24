using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Infrastructure;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class ConsolesConfigurationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddPS5Connectivity(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    private static string Failure(params (string Key, string Value)[] settings)
    {
        using var provider = Build(settings);
        return Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<ConsolesOptions>>().Value).Message;
    }

    [Fact]
    public void A_payload_manager_url_must_be_absolute_http()
    {
        var message = Failure(("PS5:Consoles:ps5:Host", "ps5"), ("PS5:Consoles:ps5:PayloadManagerUrl", "ftp://ps5/mgr"));

        Assert.Contains("PS5:Consoles:ps5", message, StringComparison.Ordinal);
        Assert.Contains("PayloadManagerUrl", message, StringComparison.Ordinal);
    }

    [Fact]
    public void At_least_one_console_is_required()
    {
        Assert.Contains("at least one console", Failure(("PS5:DefaultConsoleId", "x")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_console_id_must_be_a_slug_and_a_host_is_required()
    {
        var message = Failure(("PS5:Consoles:Bad_Id:Host", "ps5"), ("PS5:Consoles:ok:Host", ""));

        Assert.Contains("Bad_Id", message, StringComparison.Ordinal);
        Assert.Contains("PS5:Consoles:ok", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_default_console_must_exist()
    {
        var message = Failure(("PS5:Consoles:ps5:Host", "ps5"), ("PS5:DefaultConsoleId", "ghost"));

        Assert.Contains("ghost", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Consoles_get_isolated_runtimes_and_the_first_by_id_is_the_default()
    {
        using var provider = Build(("PS5:Consoles:living-room:Host", "10.0.0.2"), ("PS5:Consoles:den:Host", "10.0.0.3"));

        var registry = provider.GetRequiredService<IConsoleRegistry>();

        Assert.Equal(["den", "living-room"], registry.All.Select(c => c.Id));
        Assert.Equal("den", registry.Default.Id);
        Assert.NotSame(registry.Find("den")!.Store, registry.Find("LIVING-ROOM")!.Store);
        Assert.NotSame(registry.Find("den")!.Status, registry.Find("living-room")!.Status);
        Assert.Null(registry.Find("ghost"));
    }

    [Fact]
    public void An_explicit_default_wins()
    {
        using var provider = Build(("PS5:Consoles:a:Host", "10.0.0.2"), ("PS5:Consoles:b:Host", "10.0.0.3"), ("PS5:DefaultConsoleId", "b"));

        Assert.Equal("b", provider.GetRequiredService<IConsoleRegistry>().Default.Id);
    }
}

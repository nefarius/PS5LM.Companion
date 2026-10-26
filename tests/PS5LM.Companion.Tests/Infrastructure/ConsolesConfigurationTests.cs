using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Polly;
using Polly.Telemetry;

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
    public void No_consoles_is_a_valid_configuration()
    {
        using var provider = Build();

        Assert.Empty(provider.GetRequiredService<IConsoleRegistry>().All);
    }

    [Fact]
    public void A_console_id_must_be_a_slug_and_a_host_is_required()
    {
        var message = Failure(("PS5:Consoles:Bad_Id:Host", "ps5"), ("PS5:Consoles:ok:Host", ""));

        Assert.Contains("Bad_Id", message, StringComparison.Ordinal);
        Assert.Contains("PS5:Consoles:ok", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Consoles_get_isolated_runtimes()
    {
        using var provider = Build(("PS5:Consoles:living-room:Host", "10.0.0.2"), ("PS5:Consoles:den:Host", "10.0.0.3"));

        var registry = provider.GetRequiredService<IConsoleRegistry>();

        Assert.Equal(["den", "living-room"], registry.All.Select(c => c.Id));
        Assert.Equal(ConsoleSource.Configuration, registry.Find("den")!.Source);
        Assert.NotSame(registry.Find("den")!.Store, registry.Find("LIVING-ROOM")!.Store);
        Assert.NotSame(registry.Find("den")!.Status, registry.Find("living-room")!.Status);
        Assert.Null(registry.Find("ghost"));
    }

    [Fact]
    public void A_probe_timeout_is_a_debug_event_and_other_resilience_events_keep_their_severity()
    {
        using var provider = Build();
        var severity = provider.GetRequiredService<IOptions<TelemetryOptions>>().Value.SeverityProvider;
        Assert.NotNull(severity);

        var context = ResilienceContextPool.Shared.Get();
        try
        {
            var source = new ResilienceTelemetrySource("voidshell-voidshell", null, "Timeout");
            var timeout = new SeverityProviderArguments(source, new ResilienceEvent(ResilienceEventSeverity.Error, "OnTimeout"), context);
            var retry = new SeverityProviderArguments(source, new ResilienceEvent(ResilienceEventSeverity.Warning, "OnRetry"), context);

            Assert.Equal(ResilienceEventSeverity.Debug, severity(timeout));
            Assert.Equal(ResilienceEventSeverity.Warning, severity(retry));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }
}

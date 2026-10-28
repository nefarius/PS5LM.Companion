using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Infrastructure;

namespace PS5LM.Companion.Tests.Infrastructure;

/// <summary>Resolves the production service graph (real Refit clients, real FTP client) against closed ports.</summary>
public sealed class RealWiringTests
{
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PS5:Consoles:ps5:Host"] = "127.0.0.1",
                ["PS5:Consoles:ps5:FtpPort"] = "1",
                ["PS5:Consoles:ps5:LlamaPort"] = "1",
                ["PS5:Consoles:ps5:ExporterPort"] = "1",
                ["PS5:Consoles:ps5:ElfldrPort"] = "1",
                ["PS5:Consoles:ps5:PayloadManagerUrl"] = "http://127.0.0.1:1/",
                ["Companion:DataDirectory"] = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N")),
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddPS5Connectivity(configuration)
            .AddModelManagement(configuration)
            .BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public async Task A_console_that_answers_nothing_is_reported_disconnected()
    {
        await using var provider = Build();

        var status = await provider.GetRequiredService<IConsoleRegistry>().Find("ps5")!.Status.RefreshAsync(CancellationToken.None);

        Assert.Equal(CompanionLifecycle.Disconnected, status.Lifecycle);
        Assert.False(status.Capabilities.Ftp);
        Assert.False(status.Capabilities.Telemetry);
        Assert.False(status.Capabilities.Sensors);
        Assert.False(status.PayloadManagerAvailable);
        Assert.Equal(7, status.Dependencies.Count);
        var voidShell = status.Dependencies.Single(dependency => dependency.Id == PayloadDependencies.VoidShell);
        Assert.Equal(DependencyRequirement.Required, voidShell.Requirement);
        Assert.NotEqual(DependencyHealth.Verified, voidShell.Health);
        Assert.Equal(DependencyHealth.Unknown, status.Dependencies.Single(dependency => dependency.Id == PayloadDependencies.Elfldr).Health);
        Assert.Equal(DependencyPresence.Bundled, status.Dependencies.Single(dependency => dependency.Id == PayloadDependencies.Exporter).Presence);
        Assert.DoesNotContain(status.Dependencies, dependency => dependency.CanLaunch);
    }

    [Fact]
    public async Task The_hub_client_resolves()
    {
        await using var provider = Build();

        Assert.NotNull(provider.GetRequiredService<IModelHub>());
        Assert.NotNull(provider.GetRequiredService<ITransferStore>());
    }
}

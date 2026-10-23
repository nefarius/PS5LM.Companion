using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Tests.Support;

/// <summary>The real app wired to an in-memory console, so tests exercise routing, auth and serialisation.</summary>
internal sealed class CompanionFactory : WebApplicationFactory<PS5LM.Companion.App.Program>
{
    public FakeFileSystem FileSystem { get; } = ConsoleFixtures.PS5LMInstalled();

    public FakeLlamaProbe Llama { get; } = new();

    public FakeSensorProbe Sensors { get; } = new();

    public FakePayloadCatalog Payloads { get; } = new();

    public FakePayloadLauncher PayloadLauncher { get; } = new();

    public FakeElfldrProbe Elfldr { get; } = new();

    public FakeHub Hub { get; } = new();

    public MemoryTransferStore TransferStore { get; } = new();

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));

    public string? AccessToken { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PS5:Host", "127.0.0.1");
        builder.UseSetting("PS5:PollIntervalSeconds", "300");
        builder.UseSetting("Companion:DataDirectory", DataDirectory);
        if (AccessToken is not null)
        {
            builder.UseSetting("Companion:AccessToken", AccessToken);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPS5FileSystem>();
            services.RemoveAll<ILlamaServerProbe>();
            services.RemoveAll<IConsoleSensorProbe>();
            services.AddSingleton<IPS5FileSystem>(FileSystem);
            services.AddSingleton<ILlamaServerProbe>(Llama);
            services.AddSingleton<IConsoleSensorProbe>(Sensors);
            services.RemoveAll<IPayloadCatalog>();
            services.RemoveAll<IPayloadLauncher>();
            services.RemoveAll<IElfldrProbe>();
            services.RemoveAll<DependencyLaunchService>();
            services.AddSingleton<IPayloadCatalog>(Payloads);
            services.AddSingleton<IPayloadLauncher>(PayloadLauncher);
            services.AddSingleton<IElfldrProbe>(Elfldr);
            services.AddSingleton<DependencyLaunchService>();
            services.RemoveAll<IModelHub>();
            services.RemoveAll<ITransferStore>();
            services.AddSingleton<IModelHub>(Hub);
            services.AddSingleton<ITransferStore>(TransferStore);
        });
    }
}

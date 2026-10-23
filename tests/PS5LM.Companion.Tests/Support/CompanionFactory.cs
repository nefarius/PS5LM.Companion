using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PS5LM.Companion.Core.Abstractions;

namespace PS5LM.Companion.Tests.Support;

/// <summary>The real app wired to an in-memory console, so tests exercise routing, auth and serialisation.</summary>
internal sealed class CompanionFactory : WebApplicationFactory<PS5LM.Companion.App.Program>
{
    public FakeFileSystem FileSystem { get; } = ConsoleFixtures.PS5LMInstalled();

    public FakeLlamaProbe Llama { get; } = new();

    public FakeSensorProbe Sensors { get; } = new();

    public string? AccessToken { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PS5:Host", "127.0.0.1");
        builder.UseSetting("PS5:PollIntervalSeconds", "300");
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
        });
    }
}

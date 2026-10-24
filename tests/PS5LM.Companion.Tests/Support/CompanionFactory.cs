using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Tests.Support;

/// <summary>The real app wired to in-memory consoles, so tests exercise routing, auth and serialisation.</summary>
internal sealed class CompanionFactory : WebApplicationFactory<PS5LM.Companion.App.Program>
{
    public const string PrimaryId = "ps5";

    public FakeConnectorFactory Connectors { get; } = new();

    /// <summary>The configured console ids; the first is the default.</summary>
    public IReadOnlyList<string> ConsoleIds { get; init; } = [PrimaryId];

    public FakeConsole Primary => Connectors[ConsoleIds[0]];

    public FakeFileSystem FileSystem => Primary.FileSystem;

    public FakeLlamaProbe Llama => Primary.Llama;

    public FakeSensorProbe Sensors => Primary.Sensors;

    public FakePayloadCatalog Payloads => Primary.Payloads;

    public FakePayloadLauncher PayloadLauncher => Primary.Launcher;

    public FakeElfldrProbe Elfldr => Primary.Elfldr;

    public FakeHub Hub { get; } = new();

    public MemoryTransferStore TransferStore { get; } = new();

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));

    public string? AccessToken { get; init; }

    /// <summary>Per-console llama-server ports, for the inference gateway tests that forward to a real listener.</summary>
    public IReadOnlyDictionary<string, int> LlamaPorts { get; init; } = new Dictionary<string, int>();

    /// <summary>Extra configuration, such as <c>Inference:Pools:chat:Model</c>.</summary>
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();

    public IConsoleRegistry Registry => Services.GetRequiredService<IConsoleRegistry>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var id in ConsoleIds)
        {
            builder.UseSetting($"PS5:Consoles:{id}:Host", "127.0.0.1");
            builder.UseSetting($"PS5:Consoles:{id}:PollIntervalSeconds", "300");
        }

        foreach (var (id, port) in LlamaPorts)
        {
            builder.UseSetting($"PS5:Consoles:{id}:LlamaPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.UseSetting("PS5:DefaultConsoleId", ConsoleIds[0]);
        builder.UseSetting("Companion:DataDirectory", DataDirectory);
        if (AccessToken is not null)
        {
            builder.UseSetting("Companion:AccessToken", AccessToken);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IConsoleConnectorFactory>();
            services.AddSingleton<IConsoleConnectorFactory>(Connectors);
            services.RemoveAll<IModelHub>();
            services.RemoveAll<ITransferStore>();
            services.AddSingleton<IModelHub>(Hub);
            services.AddSingleton<ITransferStore>(TransferStore);
        });
    }
}

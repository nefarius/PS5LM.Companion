using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class PoolSyncEndpointTests
{
    private const string Models = "/data/PS5LM/models";

    private static CompanionFactory Create(params string[] consoles) => new()
    {
        ConsoleIds = consoles,
        Settings = new Dictionary<string, string>
        {
            ["Inference:Pools:chat:Model"] = "tiny-Q4.gguf",
            ["Inference:Pools:chat:Consoles:0"] = consoles[0],
            ["Inference:Pools:chat:Consoles:1"] = consoles[1],
        },
    };

    private static async Task WaitForStatusAsync(CompanionFactory factory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var console in factory.Registry.All)
        {
            while (console.Store.Current.UpdatedAt == DateTimeOffset.MinValue)
            {
                await Task.Delay(20, timeout.Token);
            }
        }
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Queues_the_recorded_download_on_consoles_without_the_model()
    {
        await using var factory = Create("office", "lab");
        factory.Connectors["office"].FileSystem
            .AddFile($"{Models}/tiny-Q4.gguf", new byte[3])
            .AddFile("/data/PS5LM/downloads.json", """[{"repo":"acme/tiny-GGUF","file":"tiny-Q4.gguf","sha256":"","bytes":3,"state":"done","save_as":""}]""");
        using var client = factory.CreateClient();
        await WaitForStatusAsync(factory);

        using var response = await client.PostAsync("/api/inference/pools/chat/sync", null);
        var body = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("acme/tiny-GGUF", body.GetProperty("source").GetProperty("repo").GetString());
        var outcomes = body.GetProperty("consoles").EnumerateArray()
            .ToDictionary(c => c.GetProperty("consoleId").GetString()!, c => c.GetProperty("outcome").GetString());
        Assert.Equal("AlreadyThere", outcomes["office"]);
        Assert.Equal("Queued", outcomes["lab"]);
        Assert.Contains(factory.Services.GetRequiredService<TransferService>().Snapshot(), t => t.ConsoleId == "lab" && t.FileName == "tiny-Q4.gguf");
    }

    [Fact]
    public async Task Succeeds_without_a_source_when_every_console_has_the_model()
    {
        await using var factory = Create("office", "lab");
        factory.Connectors["office"].FileSystem.AddFile($"{Models}/tiny-Q4.gguf", new byte[3]);
        factory.Connectors["lab"].FileSystem.AddFile($"{Models}/tiny-Q4.gguf", new byte[3]);
        using var client = factory.CreateClient();
        await WaitForStatusAsync(factory);

        using var response = await client.PostAsync("/api/inference/pools/chat/sync", null);
        var body = await BodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(body.GetProperty("consoles").EnumerateArray(), c => Assert.Equal("AlreadyThere", c.GetProperty("outcome").GetString()));
        Assert.Empty(factory.Services.GetRequiredService<TransferService>().Snapshot());
    }

    [Fact]
    public async Task Reports_conflict_when_the_source_is_unknown()
    {
        await using var factory = Create("office", "lab");
        factory.Connectors["office"].FileSystem.AddFile($"{Models}/tiny-Q4.gguf", new byte[3]);
        using var client = factory.CreateClient();
        await WaitForStatusAsync(factory);

        using var response = await client.PostAsync("/api/inference/pools/chat/sync", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<TransferService>().Snapshot());
    }

    [Fact]
    public async Task Reports_conflict_when_no_console_has_the_model()
    {
        await using var factory = Create("office", "lab");
        using var client = factory.CreateClient();
        await WaitForStatusAsync(factory);

        using var response = await client.PostAsync("/api/inference/pools/chat/sync", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_pool_is_not_found()
    {
        await using var factory = Create("office", "lab");
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/inference/pools/nope/sync", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class ModelCopyEndpointTests
{
    [Fact]
    public async Task A_model_is_copied_to_a_console_outside_any_pool()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        await using var factory = new CompanionFactory { ConsoleIds = ["ps5", "den"] };
        factory.FileSystem.AddFile("/data/PS5LM/models/a.gguf", bytes);
        _ = factory.Connectors["den"];
        using var client = factory.CreateClient();
        await factory.WaitForFirstCheckAsync();

        using var response = await client.PostAsJsonAsync(
            "/api/consoles/ps5/model-copies",
            new { paths = new[] { "/data/PS5LM/models/a.gguf" }, targets = new[] { new { consoleId = "den" } } });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var batchId = body.GetProperty("batchId").GetGuid();
        var copy = await WaitForStateAsync(client, batchId);
        Assert.Equal("Completed", copy.GetProperty("state").GetString());
        Assert.Equal(bytes, factory.Connectors["den"].FileSystem.Files["/data/PS5LM/models/a.gguf"]);
        Assert.Equal(1, factory.FileSystem.Downloads);

        using var status = await client.GetAsync($"/api/model-copies/{batchId}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var transfers = await client.GetAsync("/api/consoles/den/transfers");
        var transfer = (await transfers.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single();
        Assert.Equal("Console", transfer.GetProperty("sourceKind").GetString());
        Assert.Equal("ps5", transfer.GetProperty("sourceConsoleId").GetString());
    }

    [Fact]
    public async Task An_unknown_console_or_path_is_not_found_and_the_source_cannot_be_a_destination()
    {
        await using var factory = new CompanionFactory { ConsoleIds = ["ps5", "den"] };
        factory.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[] { 1 });
        using var client = factory.CreateClient();
        await factory.WaitForFirstCheckAsync();

        using var missing = await client.PostAsJsonAsync("/api/consoles/missing/model-copies", new { paths = new[] { "/data/PS5LM/models/a.gguf" }, targets = new[] { new { consoleId = "den" } } });
        using var path = await client.PostAsJsonAsync("/api/consoles/ps5/model-copies", new { paths = new[] { "/data/PS5LM/models/missing.gguf" }, targets = new[] { new { consoleId = "den" } } });
        using var itself = await client.PostAsJsonAsync("/api/consoles/ps5/model-copies", new { paths = new[] { "/data/PS5LM/models/a.gguf" }, targets = new[] { new { consoleId = "ps5" } } });

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, path.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, itself.StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_already_present_queues_nothing()
    {
        await using var factory = new CompanionFactory { ConsoleIds = ["ps5", "den"] };
        factory.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[] { 1 });
        factory.Connectors["den"].FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[] { 1 });
        using var client = factory.CreateClient();
        await factory.WaitForFirstCheckAsync();

        using var response = await client.PostAsJsonAsync(
            "/api/consoles/ps5/model-copies",
            new { paths = new[] { "/data/PS5LM/models/a.gguf" }, targets = new[] { new { consoleId = "den" } } });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("AlreadyPresent", body.GetProperty("items").EnumerateArray().Single().GetProperty("targets").EnumerateArray().Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Cancelling_a_batch_stops_it()
    {
        await using var factory = new CompanionFactory { ConsoleIds = ["ps5", "den"] };
        factory.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[32]);
        factory.FileSystem.DownloadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = factory.Connectors["den"];
        using var client = factory.CreateClient();
        await factory.WaitForFirstCheckAsync();

        using var created = await client.PostAsJsonAsync(
            "/api/consoles/ps5/model-copies",
            new { paths = new[] { "/data/PS5LM/models/a.gguf" }, targets = new[] { new { consoleId = "den" } } });
        var batchId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("batchId").GetGuid();
        using var cancelled = await client.DeleteAsync($"/api/model-copies/{batchId}");
        factory.FileSystem.DownloadGate.TrySetResult();

        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        Assert.False(factory.Connectors["den"].FileSystem.Has("/data/PS5LM/models/a.gguf"));
        using var missing = await client.GetAsync($"/api/model-copies/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task<JsonElement> WaitForStateAsync(HttpClient client, Guid batchId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var copy = await client.GetFromJsonAsync<JsonElement>($"/api/model-copies/{batchId}");
            var state = copy.GetProperty("state").GetString();
            if (state is "Completed" or "CompletedWithErrors" or "Failed" or "Cancelled")
            {
                return copy;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(copy.ToString());
            }

            await Task.Delay(20);
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class ModelEndpointTests
{
    private static async Task<CompanionFactory> StartedAsync(Action<CompanionFactory>? arrange = null, string? token = null)
    {
        var factory = new CompanionFactory { AccessToken = token };
        arrange?.Invoke(factory);
        var status = await factory.Services.GetRequiredService<CompanionStatusService>().RefreshAsync(CancellationToken.None);
        factory.Services.GetRequiredService<CompanionStatusStore>().Publish(status);
        return factory;
    }

    [Fact]
    public async Task Lists_models_and_upload_targets()
    {
        await using var factory = await StartedAsync(f => f.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]));
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/models");

        var model = json.GetProperty("models").EnumerateArray().Single();
        Assert.Equal("a.gguf", model.GetProperty("fileName").GetString());
        Assert.Equal("/data/PS5LM/models/a.gguf", model.GetProperty("appPath").GetString());
        Assert.Equal("Internal storage", json.GetProperty("uploadTargets")[0].GetString());
    }

    [Fact]
    public async Task Load_accepts_a_known_model_and_writes_the_command_file()
    {
        await using var factory = await StartedAsync(f => f.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]));
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/models/load", new { path = "/data/PS5LM/models/a.gguf" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/data/PS5LM/models/a.gguf\n", factory.FileSystem.Text("/data/PS5LM/load"));
    }

    [Fact]
    public async Task Load_of_an_unknown_path_is_404_and_writes_nothing()
    {
        await using var factory = await StartedAsync();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/models/load", new { path = "/etc/passwd" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.FileSystem.Text("/data/PS5LM/load"));
    }

    [Fact]
    public async Task Unload_writes_the_command_file_and_a_second_command_conflicts()
    {
        await using var factory = await StartedAsync(f => f.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]));
        using var client = factory.CreateClient();

        using var unload = await client.PostAsync("/api/models/unload", null);
        using var load = await client.PostAsJsonAsync("/api/models/load", new { path = "/data/PS5LM/models/a.gguf" });

        Assert.Equal(HttpStatusCode.Accepted, unload.StatusCode);
        Assert.Equal("-\n", factory.FileSystem.Text("/data/PS5LM/unload"));
        Assert.Equal(HttpStatusCode.Conflict, load.StatusCode);
    }

    [Fact]
    public async Task An_unreachable_console_is_503()
    {
        await using var factory = await StartedAsync();
        factory.FileSystem.Unreachable = true;
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/models/unload", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Hub_search_and_file_listing_work_and_an_unreachable_hub_is_502()
    {
        await using var factory = await StartedAsync();
        using var client = factory.CreateClient();

        var repos = await client.GetFromJsonAsync<JsonElement>("/api/hub/search?q=tiny");
        var files = await client.GetFromJsonAsync<JsonElement>("/api/hub/files?repo=acme/tiny-GGUF");
        using var invalid = await client.GetAsync("/api/hub/files?repo=nope");
        factory.Hub.Unavailable = true;
        using var down = await client.GetAsync("/api/hub/search?q=tiny");

        Assert.Equal("acme/tiny-GGUF", repos[0].GetProperty("id").GetString());
        Assert.Equal("tiny-Q4.gguf", files[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode);
    }

    [Fact]
    public async Task A_transfer_can_be_created_listed_and_cancelled_with_string_enums()
    {
        await using var factory = await StartedAsync();
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/api/transfers", new { repo = "acme/tiny-GGUF", fileName = "tiny-Q4.gguf" });
        var transfer = await created.Content.ReadFromJsonAsync<JsonElement>();
        var list = await client.GetFromJsonAsync<JsonElement>("/api/transfers");
        using var cancelled = await client.DeleteAsync($"/api/transfers/{transfer.GetProperty("id").GetString()}");
        using var invalid = await client.PostAsJsonAsync("/api/transfers", new { repo = "bad", fileName = "x.gguf" });

        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        Assert.Equal("Queued", transfer.GetProperty("state").GetString());
        Assert.Single(list.EnumerateArray());
        // The worker may already have finished the instant transfer: both answers mean the cancel was handled.
        Assert.Contains(cancelled.StatusCode, new[] { HttpStatusCode.Accepted, HttpStatusCode.OK });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task The_model_api_requires_the_token_when_configured()
    {
        await using var factory = await StartedAsync(token: "secret");
        using var client = factory.CreateClient();

        using var anonymous = await client.PostAsync("/api/models/unload", null);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(factory.FileSystem.Text("/data/PS5LM/unload"));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class ModelEndpointTests
{
    private static async Task<CompanionFactory> StartedAsync(Action<CompanionFactory>? arrange = null, string? token = null)
    {
        var factory = new CompanionFactory { AccessToken = token };
        arrange?.Invoke(factory);
        foreach (var console in factory.Registry.All)
        {
            console.Store.Publish(await console.Status.RefreshAsync(CancellationToken.None));
        }

        return factory;
    }

    [Fact]
    public async Task Lists_models_and_upload_targets()
    {
        await using var factory = await StartedAsync(f => f.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]));
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/consoles/ps5/models");

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

        using var response = await client.PostAsJsonAsync("/api/consoles/ps5/models/load", new { path = "/data/PS5LM/models/a.gguf" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/data/PS5LM/models/a.gguf\n", factory.FileSystem.Text("/data/PS5LM/load"));
    }

    [Fact]
    public async Task Load_of_an_unknown_path_is_404_and_writes_nothing()
    {
        await using var factory = await StartedAsync();
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/consoles/ps5/models/load", new { path = "/etc/passwd" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(factory.FileSystem.Text("/data/PS5LM/load"));
    }

    [Fact]
    public async Task Unload_writes_the_command_file_and_a_second_command_conflicts()
    {
        await using var factory = await StartedAsync(f => f.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]));
        using var client = factory.CreateClient();

        using var unload = await client.PostAsync("/api/consoles/ps5/models/unload", null);
        using var load = await client.PostAsJsonAsync("/api/consoles/ps5/models/load", new { path = "/data/PS5LM/models/a.gguf" });

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

        using var response = await client.PostAsync("/api/consoles/ps5/models/unload", null);

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

        using var created = await client.PostAsJsonAsync("/api/consoles/ps5/transfers", new { repo = "acme/tiny-GGUF", fileName = "tiny-Q4.gguf" });
        var transfer = await created.Content.ReadFromJsonAsync<JsonElement>();
        var list = await client.GetFromJsonAsync<JsonElement>("/api/consoles/ps5/transfers");
        using var cancelled = await client.DeleteAsync($"/api/consoles/ps5/transfers/{transfer.GetProperty("id").GetString()}");
        using var invalid = await client.PostAsJsonAsync("/api/consoles/ps5/transfers", new { repo = "bad", fileName = "x.gguf" });

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

        using var anonymous = await client.PostAsync("/api/consoles/ps5/models/unload", null);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Null(factory.FileSystem.Text("/data/PS5LM/unload"));
    }

    [Fact]
    public async Task Delete_removes_one_file_and_leaves_its_companion()
    {
        await using var factory = await StartedAsync(f => f.FileSystem
            .AddFile("/data/PS5LM/models/d1-omni-600M-BF16.gguf", new byte[4])
            .AddFile("/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf", new byte[2]));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Delete("/api/consoles/ps5/models", "/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Restart the PS5 app", body.GetProperty("message").GetString());
        Assert.False(factory.FileSystem.Files.ContainsKey("/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf"));
        Assert.True(factory.FileSystem.Files.ContainsKey("/data/PS5LM/models/d1-omni-600M-BF16.gguf"));
    }

    [Fact]
    public async Task Delete_rejects_an_unknown_path_a_loaded_model_and_a_pending_command()
    {
        await using var factory = await StartedAsync(f => f.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]));
        using var client = factory.CreateClient();

        using var unknown = await client.SendAsync(Delete("/api/consoles/ps5/models", "/etc/passwd"));
        factory.Registry.Default.Store.Publish(factory.Registry.Default.Store.Current with { LoadedModel = "a.gguf" });
        using var loaded = await client.SendAsync(Delete("/api/consoles/ps5/models", "/data/PS5LM/models/a.gguf"));
        factory.Registry.Default.Store.Publish(factory.Registry.Default.Store.Current with { LoadedModel = null });
        factory.FileSystem.AddFile("/data/PS5LM/load", "queued\n");
        using var pending = await client.SendAsync(Delete("/api/consoles/ps5/models", "/data/PS5LM/models/a.gguf"));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, loaded.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.True(factory.FileSystem.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
    }

    [Fact]
    public async Task Delete_of_a_read_only_model_is_400()
    {
        await using var factory = new CompanionFactory();
        var fs = ConsoleFixtures.PS5LMPlusInstalled().AddFile("/data/homebrew/PPSA99581/models/bundled.gguf", new byte[1]);
        factory.Connectors.Replace(CompanionFactory.PrimaryId, new FakeConsole(fs));
        foreach (var console in factory.Registry.All)
        {
            console.Store.Publish(await console.Status.RefreshAsync(CancellationToken.None));
        }

        using var client = factory.CreateClient();
        using var response = await client.SendAsync(Delete("/api/consoles/ps5/models", "/app0/models/bundled.gguf"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(fs.Files.ContainsKey("/data/homebrew/PPSA99581/models/bundled.gguf"));
    }

    [Fact]
    public async Task Delete_conflicts_with_an_active_transfer_of_the_same_file()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = await StartedAsync(f =>
        {
            f.FileSystem.AddFile("/data/PS5LM/models/tiny-Q4.gguf", new byte[3]);
            f.FileSystem.UploadGate = gate;
            var now = DateTimeOffset.UtcNow;
            f.TransferStore.Saved =
            [
                new Transfer(
                    Guid.NewGuid(),
                    "ps5",
                    "acme/tiny-GGUF",
                    "tiny-Q4.gguf",
                    FakeHub.Sha256,
                    FakeHub.Content.Length,
                    "Internal storage",
                    TransferState.Queued,
                    0,
                    0,
                    null,
                    now,
                    now),
            ];
        });
        using var client = factory.CreateClient();
        var transfers = factory.Services.GetRequiredService<TransferService>();
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!transfers.Snapshot("ps5").Any(t => t.FileName == "tiny-Q4.gguf" && t.IsActive))
        {
            await Task.Delay(20, wait.Token);
        }

        try
        {
            using var response = await client.SendAsync(Delete("/api/consoles/ps5/models", "/data/PS5LM/models/tiny-Q4.gguf"));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.True(factory.FileSystem.Files.ContainsKey("/data/PS5LM/models/tiny-Q4.gguf"));
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Delete_requires_the_token_when_configured()
    {
        await using var factory = await StartedAsync(
            f => f.FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]),
            token: "secret");
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync(Delete("/api/consoles/ps5/models", "/data/PS5LM/models/a.gguf"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(factory.FileSystem.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
    }

    private static HttpRequestMessage Delete(string url, string path) => new(HttpMethod.Delete, url)
    {
        Content = JsonContent.Create(new { path }),
    };
}

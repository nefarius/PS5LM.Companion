using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class MultiConsoleEndpointTests
{
    private static CompanionFactory TwoConsoles() => new() { ConsoleIds = ["living-room", "den"] };

    private static async Task PublishAsync(CompanionFactory factory)
    {
        foreach (var console in factory.Registry.All)
        {
            console.Store.Publish(await console.Status.RefreshAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Consoles_are_listed_with_the_default_marked()
    {
        await using var factory = TwoConsoles();
        await PublishAsync(factory);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/consoles");

        var consoles = json.EnumerateArray().ToList();
        Assert.Equal(["den", "living-room"], consoles.Select(c => c.GetProperty("id").GetString()));
        Assert.Equal(["living-room"], consoles.Where(c => c.GetProperty("isDefault").GetBoolean()).Select(c => c.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Status_and_models_are_isolated_per_console()
    {
        await using var factory = TwoConsoles();
        factory.Connectors["den"].FileSystem.AddFile("/data/PS5LM/models/den.gguf", new byte[3]);
        factory.Connectors["living-room"].FileSystem.AddFile("/data/PS5LM/models/room.gguf", new byte[3]);
        factory.Connectors["den"].FileSystem.Unreachable = false;
        await PublishAsync(factory);
        factory.Connectors["living-room"].FileSystem.Unreachable = true;
        await factory.Registry.Find("living-room")!.Status.RefreshAsync(CancellationToken.None)
            .ContinueWith(t => factory.Registry.Find("living-room")!.Store.Publish(t.Result));
        using var client = factory.CreateClient();

        var den = await client.GetFromJsonAsync<JsonElement>("/api/consoles/den/models");
        var denStatus = await client.GetFromJsonAsync<JsonElement>("/api/consoles/den/status");
        var roomStatus = await client.GetFromJsonAsync<JsonElement>("/api/consoles/living-room/status");

        Assert.Equal("den.gguf", den.GetProperty("models").EnumerateArray().Single().GetProperty("fileName").GetString());
        Assert.Equal("AppAvailableNoModel", denStatus.GetProperty("status").GetProperty("lifecycle").GetString());
        Assert.Equal("Disconnected", roomStatus.GetProperty("status").GetProperty("lifecycle").GetString());
    }

    [Fact]
    public async Task A_command_only_reaches_the_addressed_console()
    {
        await using var factory = TwoConsoles();
        factory.Connectors["den"].FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]);
        factory.Connectors["living-room"].FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]);
        await PublishAsync(factory);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/consoles/den/models/load", new { path = "/data/PS5LM/models/a.gguf" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/data/PS5LM/models/a.gguf\n", factory.Connectors["den"].FileSystem.Text("/data/PS5LM/load"));
        Assert.Null(factory.Connectors["living-room"].FileSystem.Text("/data/PS5LM/load"));
    }

    [Fact]
    public async Task Delete_removes_the_file_only_on_the_addressed_console()
    {
        await using var factory = TwoConsoles();
        factory.Connectors["den"].FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]);
        factory.Connectors["living-room"].FileSystem.AddFile("/data/PS5LM/models/a.gguf", new byte[3]);
        await PublishAsync(factory);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/consoles/den/models")
        {
            Content = JsonContent.Create(new { path = "/data/PS5LM/models/a.gguf" }),
        };

        using var response = await client.SendAsync(request);
        using var unknown = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/consoles/ghost/models")
        {
            Content = JsonContent.Create(new { path = "/data/PS5LM/models/a.gguf" }),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(factory.Connectors["den"].FileSystem.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
        Assert.True(factory.Connectors["living-room"].FileSystem.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/consoles/ghost/status")]
    [InlineData("GET", "/api/consoles/ghost/models")]
    [InlineData("POST", "/api/consoles/ghost/models/unload")]
    [InlineData("GET", "/api/consoles/ghost/dependencies")]
    [InlineData("POST", "/api/consoles/ghost/dependencies/etahen/launch")]
    [InlineData("GET", "/api/consoles/ghost/transfers")]
    [InlineData("POST", "/api/consoles/ghost/transfers")]
    public async Task An_unknown_console_is_404(string method, string url)
    {
        await using var factory = TwoConsoles();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { repo = "acme/tiny-GGUF", fileName = "tiny-Q4.gguf" });
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Transfers_are_scoped_to_their_console()
    {
        await using var factory = TwoConsoles();
        await PublishAsync(factory);
        using var client = factory.CreateClient();

        using var created = await client.PostAsJsonAsync("/api/consoles/den/transfers", new { repo = "acme/tiny-GGUF", fileName = "tiny-Q4.gguf" });
        var transfer = await created.Content.ReadFromJsonAsync<JsonElement>();
        var den = await client.GetFromJsonAsync<JsonElement>("/api/consoles/den/transfers");
        var room = await client.GetFromJsonAsync<JsonElement>("/api/consoles/living-room/transfers");
        using var wrongConsole = await client.DeleteAsync($"/api/consoles/living-room/transfers/{transfer.GetProperty("id").GetString()}");

        Assert.Equal("den", transfer.GetProperty("consoleId").GetString());
        Assert.Single(den.EnumerateArray());
        Assert.Empty(room.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, wrongConsole.StatusCode);
    }

    [Fact]
    public async Task The_removed_single_console_routes_are_gone()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var status = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class ConsoleManagementEndpointTests
{
    [Fact]
    public async Task The_app_starts_with_no_consoles_and_a_user_console_can_be_managed()
    {
        await using var factory = new CompanionFactory { ConsoleIds = [] };
        using var client = factory.CreateClient();

        Assert.Empty(factory.Registry.All);

        using var created = await client.PostAsJsonAsync("/api/consoles", new
        {
            id = "Den",
            settings = new { host = "10.0.0.8", displayName = "Den" },
        });
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/consoles/den/settings");

        using var updated = await client.PutAsJsonAsync("/api/consoles/den", new { host = "10.0.0.9", displayName = "Den" });
        var afterUpdate = await client.GetFromJsonAsync<JsonElement>("/api/consoles/den/settings");
        using var duplicate = await client.PostAsJsonAsync("/api/consoles", new { id = "den", settings = new { host = "10.0.0.1" } });
        var stored = File.ReadAllText(Path.Combine(factory.DataDirectory, "consoles.json"));
        using var removed = await client.DeleteAsync("/api/consoles/den");
        using var missing = await client.GetAsync("/api/consoles/den/settings");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/consoles/den", created.Headers.Location?.OriginalString);
        Assert.Equal("den", createdBody.GetProperty("id").GetString());
        Assert.Equal("User", createdBody.GetProperty("source").GetString());
        Assert.Equal("User", settings.GetProperty("source").GetString());
        Assert.Equal("10.0.0.8", settings.GetProperty("settings").GetProperty("host").GetString());
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("10.0.0.9", afterUpdate.GetProperty("settings").GetProperty("host").GetString());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("\"id\": \"den\"", stored, StringComparison.Ordinal);
        Assert.Equal("[]", File.ReadAllText(Path.Combine(factory.DataDirectory, "consoles.json")).Trim());
    }

    [Fact]
    public async Task A_configuration_console_cannot_be_edited_or_removed()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var updated = await client.PutAsJsonAsync("/api/consoles/ps5", new { host = "10.0.0.9" });
        using var removed = await client.DeleteAsync("/api/consoles/ps5");
        using var invalid = await client.PostAsJsonAsync("/api/consoles", new { id = "Bad Id", settings = new { host = "10.0.0.1" } });

        Assert.Equal(HttpStatusCode.Conflict, updated.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.NotNull(factory.Registry.Find("ps5"));
    }

    [Fact]
    public async Task Removing_a_console_with_a_transfer_is_rejected()
    {
        await using var factory = new CompanionFactory { ConsoleIds = [] };
        factory.Hub.DownloadGate = new TaskCompletionSource<bool>();
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/consoles", new { id = "den", settings = new { host = "10.0.0.8" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await WaitForStatusAsync(factory);

        using var transfer = await client.PostAsJsonAsync("/api/consoles/den/transfers", new { repo = "acme/tiny-GGUF", fileName = "tiny-Q4.gguf" });
        using var removed = await client.DeleteAsync("/api/consoles/den");

        Assert.Equal(HttpStatusCode.Accepted, transfer.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);
        Assert.NotNull(factory.Registry.Find("den"));
        factory.Hub.DownloadGate.TrySetResult(true);
    }

    private static async Task WaitForStatusAsync(CompanionFactory factory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var console = factory.Registry.Find("den")!;
        while (console.Store.Current.UpdatedAt == DateTimeOffset.MinValue)
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}

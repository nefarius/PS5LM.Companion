using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class AppEndpointTests
{
    private static async Task PublishAsync(CompanionFactory factory)
    {
        foreach (var console in factory.Registry.All)
        {
            console.Store.Publish(await console.Status.RefreshAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Apps_are_listed_and_launched_through_VoidShell()
    {
        await using var factory = new CompanionFactory();
        factory.Primary.VoidShell.SetUp(["PPSA99575", "PPSA99581"], active: "PPSA9957");
        await PublishAsync(factory);
        using var client = factory.CreateClient();

        var apps = await client.GetFromJsonAsync<JsonElement>("/api/consoles/ps5/apps");
        var launch = await client.PostAsync("/api/consoles/ps5/apps/ps5lmplus/launch", null);

        Assert.Equal("VoidShell", apps.GetProperty("provider").GetString());
        Assert.Equal(2, apps.GetProperty("apps").GetArrayLength());
        Assert.Equal(HttpStatusCode.Accepted, launch.StatusCode);
        Assert.Equal(["PPSA99581"], factory.Primary.VoidShell.Launched);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("unknown")]
    [InlineData("1")]
    public async Task An_invalid_variant_is_rejected(string variant)
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync($"/api/consoles/ps5/apps/{variant}/launch", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Launch_is_isolated_per_console_and_unknown_consoles_are_404()
    {
        await using var factory = new CompanionFactory { ConsoleIds = ["a", "b"] };
        factory.Connectors["a"].VoidShell.SetUp(["PPSA99575"]);
        factory.Connectors["b"].VoidShell.SetUp(["PPSA99575"]);
        using var client = factory.CreateClient();

        var launch = await client.PostAsync("/api/consoles/b/apps/ps5lm/launch", null);
        var missing = await client.GetAsync("/api/consoles/zzz/apps");

        Assert.Equal(HttpStatusCode.Accepted, launch.StatusCode);
        Assert.Empty(factory.Connectors["a"].VoidShell.Launched);
        Assert.Single(factory.Connectors["b"].VoidShell.Launched);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Launch_without_a_reachable_service_is_503()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/consoles/ps5/apps/ps5lm/launch", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

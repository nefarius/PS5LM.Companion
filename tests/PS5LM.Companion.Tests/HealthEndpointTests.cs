using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;

namespace PS5LM.Companion.Tests;

public sealed class HealthEndpointTests(WebApplicationFactory<PS5LM.Companion.App.Program> factory)
    : IClassFixture<WebApplicationFactory<PS5LM.Companion.App.Program>>
{
    [Fact]
    public async Task Health_returns_ok()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

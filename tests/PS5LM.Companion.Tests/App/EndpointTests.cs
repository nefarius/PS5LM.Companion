using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class EndpointTests
{
    private static async Task PublishAsync(CompanionFactory factory)
    {
        var status = await factory.Services.GetRequiredService<CompanionStatusService>().RefreshAsync(CancellationToken.None);
        factory.Services.GetRequiredService<CompanionStatusStore>().Publish(status);
    }

    [Fact]
    public async Task Health_is_open_and_ok()
    {
        await using var factory = new CompanionFactory { AccessToken = "secret" };
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Status_reports_the_pre_model_lifecycle_with_string_enums()
    {
        await using var factory = new CompanionFactory();
        await PublishAsync(factory);
        using var client = factory.CreateClient();

        var json = await client.GetFromJsonAsync<JsonElement>("/api/status");

        Assert.Equal("AppAvailableNoModel", json.GetProperty("lifecycle").GetString());
        Assert.Equal("PS5LM", json.GetProperty("variant").GetString());
        Assert.True(json.GetProperty("capabilities").GetProperty("ftp").GetBoolean());
    }

    [Fact]
    public async Task Api_requires_the_token_when_configured()
    {
        await using var factory = new CompanionFactory { AccessToken = "secret" };
        using var client = factory.CreateClient();

        using var anonymous = await client.GetAsync("/api/status");
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        using var wrongResponse = await client.SendAsync(wrong);
        using var right = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        right.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        using var rightResponse = await client.SendAsync(right);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rightResponse.StatusCode);
    }

    [Fact]
    public async Task Browsers_are_redirected_to_the_login_page_and_get_a_cookie_after_signing_in()
    {
        await using var factory = new CompanionFactory { AccessToken = "secret" };
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var redirect = await client.GetAsync("/");
        using var rejected = await client.PostAsync("/login", new FormUrlEncodedContent([new("token", "nope")]));
        using var accepted = await client.PostAsync("/login", new FormUrlEncodedContent([new("token", "secret")]));

        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/login", redirect.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Contains(accepted.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ps5lm_token=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }
}

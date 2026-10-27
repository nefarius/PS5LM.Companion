using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class EndpointTests
{
    private const string Base = "/api/consoles/ps5";
    private const string StatusUrl = Base + "/status";

    private static async Task PublishAsync(CompanionFactory factory)
    {
        foreach (var console in factory.Registry.All)
        {
            console.Store.Publish(await console.Status.RefreshAsync(CancellationToken.None));
        }
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

        var json = (await client.GetFromJsonAsync<JsonElement>(StatusUrl)).GetProperty("status");

        Assert.Equal("AppAvailableNoModel", json.GetProperty("lifecycle").GetString());
        Assert.Equal("PS5LM", json.GetProperty("variant").GetString());
        Assert.True(json.GetProperty("capabilities").GetProperty("ftp").GetBoolean());
    }

    [Fact]
    public async Task Api_requires_the_token_when_configured()
    {
        await using var factory = new CompanionFactory { AccessToken = "secret" };
        using var client = factory.CreateClient();

        using var anonymous = await client.GetAsync(StatusUrl);
        using var wrong = new HttpRequestMessage(HttpMethod.Get, StatusUrl);
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        using var wrongResponse = await client.SendAsync(wrong);
        using var right = new HttpRequestMessage(HttpMethod.Get, StatusUrl);
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
        Assert.Contains("<title>PS5LM Companion - Sign in</title>", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        Assert.Contains(accepted.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ps5lm_token=", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Dependencies_are_part_of_the_status_and_have_their_own_route()
    {
        await using var factory = new CompanionFactory();
        await PublishAsync(factory);
        using var client = factory.CreateClient();

        var status = (await client.GetFromJsonAsync<JsonElement>(StatusUrl)).GetProperty("status");
        var dependencies = await client.GetFromJsonAsync<JsonElement>($"{Base}/dependencies");

        Assert.False(status.GetProperty("payloadManagerAvailable").GetBoolean());
        Assert.Equal(6, status.GetProperty("dependencies").GetArrayLength());
        Assert.Equal("kstuff-lite", dependencies.GetProperty("dependencies")[0].GetProperty("id").GetString());
        Assert.Equal("Required", dependencies.GetProperty("dependencies")[0].GetProperty("requirement").GetString());
        Assert.False(dependencies.GetProperty("dependencies")[0].GetProperty("canLaunch").GetBoolean());
    }

    [Fact]
    public async Task Launch_requires_the_token_and_sends_the_installed_slot()
    {
        await using var factory = new CompanionFactory { AccessToken = "secret" };
        factory.Payloads.Snapshot = new PayloadCatalogSnapshot(
            true,
            true,
            null,
            [new InstalledPayload(PayloadDependencies.EtaHen, "repo/etahen@abc", "etaHEN")],
            new HashSet<string>());
        factory.Elfldr.Listening = true;
        using var client = factory.CreateClient();

        using var anonymous = await client.PostAsync($"{Base}/dependencies/etahen/launch", null);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/dependencies/etahen/launch");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        using var accepted = await client.SendAsync(request);
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Contains("hand-off", body.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(["repo/etahen@abc"], factory.PayloadLauncher.Launched);
    }
}

using System.Net;

using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class DashboardTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/models")]
    [InlineData("/transfers")]
    public async Task Pages_render_the_shell_with_MudBlazor(string path)
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("PS5LM Companion", html);
        Assert.Contains("_content/MudBlazor/MudBlazor.min.js", html);
        Assert.Contains("_framework/blazor.web.js", html);
    }

    [Fact]
    public async Task The_dashboard_is_behind_the_access_token()
    {
        await using var factory = new CompanionFactory { AccessToken = "secret" };
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var anonymous = await client.GetAsync("/models");
        using var circuit = await client.PostAsync("/_blazor/negotiate", null);
        using var withCookie = new HttpRequestMessage(HttpMethod.Get, "/models");
        withCookie.Headers.Add("Cookie", "ps5lm_token=secret");
        using var signedIn = await client.SendAsync(withCookie);

        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, circuit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }
}

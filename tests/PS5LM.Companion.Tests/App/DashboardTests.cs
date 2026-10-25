using System.Net;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class DashboardTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/consoles/ps5")]
    [InlineData("/consoles/ps5/models")]
    [InlineData("/consoles/ps5/downloads")]
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
        Assert.Contains("<h1", html);
        Assert.Contains("aria-label=\"Toggle navigation menu\"", html);
    }

    [Fact]
    public async Task The_status_page_explains_the_payload_launch_limit()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/consoles/ps5");

        Assert.Contains("Payloads", html);
        Assert.Contains("kstuff-lite", html);
        Assert.Contains("only means Payload Manager sent the file", html);
        Assert.Contains("ps5 · 127.0.0.1", html);
    }

    [Fact]
    public async Task An_unknown_console_explains_itself()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        var status = await client.GetStringAsync("/consoles/missing");
        var models = await client.GetStringAsync("/consoles/missing/models");
        var downloads = await client.GetStringAsync("/consoles/missing/downloads");

        Assert.Contains("Console not found", status);
        Assert.Contains("Back to overview", status);
        Assert.Contains("Console not found", models);
        Assert.Contains("Console not found", downloads);
    }

    [Fact]
    public async Task The_drawer_links_each_console_to_its_pages()
    {
        await using var factory = new CompanionFactory { ConsoleIds = ["living-room", "den"] };
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/consoles/living-room");

        Assert.Contains("living-room", html);
        Assert.Contains("den", html);
        Assert.Contains("consoles/living-room/models", html);
        Assert.Contains("consoles/den/downloads", html);
        Assert.Contains("living-room · 127.0.0.1", html);
        Assert.DoesNotContain("den · 127.0.0.1", html);
    }

    [Theory]
    [InlineData("/models")]
    [InlineData("/transfers")]
    public async Task The_old_pages_are_gone(string path)
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task One_console_opens_straight_on_its_status_page()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("/consoles/ps5", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_overview_summarises_every_console()
    {
        await using var factory = new CompanionFactory { ConsoleIds = ["den", "room"] };
        // The first refresh publishes on its own. Wait for it, then replace both, so the next poll (minutes away) cannot overwrite the page.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (factory.Registry.All.Any(console => console.Store.Current.UpdatedAt == DateTimeOffset.MinValue))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The status monitor did not publish a first check.");
            }

            await Task.Delay(20);
        }

        var checkedAt = DateTimeOffset.UtcNow;
        factory.Registry.Find("den")!.Store.Publish(CompanionStatus.Initial with
        {
            Lifecycle = CompanionLifecycle.Ready,
            Variant = PS5Variant.PS5LM,
            LoadedModel = "qwen.gguf",
            Telemetry = new TelemetrySnapshot(10, 4, 0, 12.5, 40, checkedAt),
            Sensors = new ConsoleSensors(61.2, null, null, null, [new DriveSpace("/data", 1000, 250)]),
            UpdatedAt = checkedAt,
            PayloadManagerAvailable = false,
            Dependencies =
            [
                new DependencyStatus("kstuff-lite", "kstuff-lite", DependencyRequirement.Required, DependencyPresence.NotInstalled, DependencyHealth.Unavailable, false, "Missing"),
            ],
        });
        factory.Registry.Find("room")!.Store.Publish(CompanionStatus.Initial with
        {
            Lifecycle = CompanionLifecycle.Disconnected,
            UpdatedAt = checkedAt,
            PayloadManagerAvailable = true,
            Dependencies = [],
        });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("Overview", html);
        Assert.Contains("2 consoles", html);
        Assert.Contains("1 ready", html);
        Assert.Contains("0 generating", html);
        Assert.Contains("1 offline", html);
        Assert.Contains("den", html);
        Assert.Contains("room", html);
        Assert.Contains("qwen.gguf", html);
        Assert.Contains("12.5 tokens/s", html);
        Assert.Contains("61.2", html);
        Assert.Contains("250 B free", html);
        Assert.Contains("Unload the model on den", html);
        Assert.Contains("Payload Manager unreachable", html);
        Assert.Contains("Required payloads missing", html);
    }

    [Fact]
    public async Task The_dashboard_is_behind_the_access_token()
    {
        await using var factory = new CompanionFactory { AccessToken = "secret" };
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        using var anonymous = await client.GetAsync("/consoles/ps5/models");
        using var circuit = await client.PostAsync("/_blazor/negotiate", null);
        using var withCookie = new HttpRequestMessage(HttpMethod.Get, "/consoles/ps5/models");
        withCookie.Headers.Add("Cookie", "ps5lm_token=secret");
        using var signedIn = await client.SendAsync(withCookie);

        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, circuit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }
}

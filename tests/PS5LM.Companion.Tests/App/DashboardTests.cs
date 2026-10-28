using System.Net;
using System.Reflection;
using System.Text;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using PS5LM.Companion.App.Updates;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
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
        Assert.Contains("app-brand", html);
        Assert.Contains("rel=\"icon\"", html);
        Assert.Contains("apple-touch-icon", html);
        Assert.Contains("_content/MudBlazor/MudBlazor.min.js", html);
        Assert.Contains("_framework/blazor.web.js", html);
        Assert.Contains("<h1", html);
        Assert.Contains("aria-label=\"Toggle navigation menu\"", html);
        var version = typeof(PS5LM.Companion.App.Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Contains("app-version", html);
        Assert.Contains("mud-tooltip-root", html);
        Assert.Contains($"aria-label=\"Version v{version}\"", html);
        Assert.Contains($"v{version}", html);
        Assert.Contains("href=\"https://github.com/nefarius/PS5LM.Companion/\"", html);
        Assert.Contains("aria-label=\"PS5LM Companion on GitHub\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public async Task An_interactive_render_links_the_version_chip_to_a_newer_release()
    {
        const string releaseUrl = "https://github.com/nefarius/PS5LM.Companion/releases/tag/v9.9.9";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"tag_name":"v9.9.9","html_url":"{{releaseUrl}}","draft":false,"prerelease":false}""",
                Encoding.UTF8,
                "application/json"),
        });
        var http = new HttpClient(handler, disposeHandler: false);
        var checker = new LatestReleaseChecker(http, new FixedVersion("1.0.0"));
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(checker);
        ctx.Services.AddSingleton<IConsoleRegistry>(new EmptyRegistry());
        ctx.Services.AddMudServices();

        var cut = ctx.Render<PS5LM.Companion.App.Components.Layout.MainLayout>();

        cut.WaitForAssertion(() =>
        {
            var chip = cut.Find(".app-version");
            Assert.Contains("mud-chip-color-warning", chip.GetAttribute("class"));
            Assert.Equal(releaseUrl, chip.GetAttribute("href"));
            Assert.Equal("_blank", chip.GetAttribute("target"));
            Assert.Contains("noopener", chip.GetAttribute("rel"));
            Assert.Contains("v1.0.0 · v9.9.9 available", chip.TextContent);
        });
    }

    [Fact]
    public async Task The_status_page_shows_the_console_firmware()
    {
        await using var factory = new CompanionFactory();
        var console = factory.Registry.Find("ps5")!;
        console.Store.Publish(CompanionStatus.Initial with { FirmwareVersion = "10.01", UpdatedAt = DateTimeOffset.UtcNow });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/consoles/ps5");

        Assert.Contains("<td>Firmware</td><td>10.01</td>", html);
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
    [InlineData("/consoles/ps5", "PS5LM Companion - Status - ps5")]
    [InlineData("/consoles/ps5/models", "PS5LM Companion - Models - ps5")]
    [InlineData("/consoles/ps5/downloads", "PS5LM Companion - Downloads - ps5")]
    [InlineData("/inference", "PS5LM Companion - Inference gateway")]
    [InlineData("/settings/consoles", "PS5LM Companion - Consoles")]
    [InlineData("/consoles/missing", "PS5LM Companion - Console not found")]
    public async Task Every_page_title_is_prefixed_with_the_app_name(string path, string title)
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains($"<title>{title}</title>", html);
    }

    [Fact]
    public async Task The_overview_title_is_prefixed_with_the_app_name()
    {
        await using var factory = new CompanionFactory { ConsoleIds = ["den", "room"] };
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("<title>PS5LM Companion - Overview</title>", html);
    }

    [Fact]
    public async Task An_unknown_address_renders_the_not_found_page()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/no-such-page");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("<title>PS5LM Companion - Page not found</title>", html);
        Assert.Contains("This page does not exist.", html);
        Assert.Contains("app-brand", html);
    }

    [Theory]
    [InlineData("POST", "/no-such-page", HttpStatusCode.MethodNotAllowed)]
    [InlineData("HEAD", "/no-such-page", HttpStatusCode.NotFound)]
    [InlineData("GET", "/API/missing", HttpStatusCode.NotFound)]
    [InlineData("GET", "/V1/missing", HttpStatusCode.NotFound)]
    [InlineData("GET", "/_Blazor/missing", HttpStatusCode.NotFound)]
    [InlineData("GET", "/Health/missing", HttpStatusCode.NotFound)]
    public async Task Non_get_and_mixed_case_service_misses_are_not_the_not_found_page(string method, string path, HttpStatusCode status)
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(status, response.StatusCode);
        Assert.DoesNotContain("Page not found", body);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
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
            FirmwareVersion = "13.40",
            Telemetry = new TelemetrySnapshot(10, 4, 0, 12.5, 40, checkedAt),
            Sensors = new ConsoleSensors(61.2, null, null, null,
            [
                new DriveSpace("/system_tmp", 2_000_000, 100),
                new DriveSpace("/data", 1000, 250),
            ]),
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
        Assert.Contains("<td>Firmware</td><td>13.40</td>", html);
        Assert.Contains("<td>Firmware</td><td>n/a</td>", html);
        Assert.Matches(@"12[.,]5 tokens/s", html);
        Assert.Matches(@"61[.,]2", html);
        Assert.Contains("250 B free", html);
        Assert.DoesNotContain("100 B free", html);
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

    private sealed class FixedVersion(string? version) : IAssemblyVersionSource
    {
        public string? TryGetVersion() => version;
    }

    private sealed class EmptyRegistry : IConsoleRegistry
    {
        public IReadOnlyList<ConsoleRuntime> All { get; } = [];

        public event Action<ConsoleChange>? Changed { add { } remove { } }

        public ConsoleRuntime? Find(string? id) => null;

        public ConsoleEditResult Add(string id, PS5Options options) => throw new NotSupportedException();

        public ConsoleEditResult Update(string id, PS5Options options) => throw new NotSupportedException();

        public ConsoleEditResult Remove(string id) => throw new NotSupportedException();
    }
}

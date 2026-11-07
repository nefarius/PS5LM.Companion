using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;
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
        Assert.DoesNotContain("Page not found", await anonymous.Content.ReadAsStringAsync());
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
        Assert.Equal(7, status.GetProperty("dependencies").GetArrayLength());
        var voidShell = dependencies.GetProperty("dependencies").EnumerateArray().Single(row => row.GetProperty("id").GetString() == "voidshell");
        Assert.Equal("Required", voidShell.GetProperty("requirement").GetString());
        Assert.False(voidShell.GetProperty("canLaunch").GetBoolean());
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

    [Fact]
    public async Task Updates_are_part_of_the_status_and_have_their_own_route()
    {
        await using var factory = new CompanionFactory();
        factory.Registry.Find("ps5")!.Store.Publish(CompanionStatus.Initial with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Updates = new ComponentUpdateStatus(
                [new ComponentUpdate("pldmgr", "Payload Manager", "0.6.3", "0.6.0 -> 0.6.3")],
                DateTimeOffset.UtcNow,
                false,
                null),
        });
        using var client = factory.CreateClient();

        var status = (await client.GetFromJsonAsync<JsonElement>(StatusUrl)).GetProperty("status");
        var updates = await client.GetFromJsonAsync<JsonElement>($"{Base}/updates");

        Assert.Equal("pldmgr", status.GetProperty("updates").GetProperty("outdated")[0].GetProperty("id").GetString());
        Assert.Equal("pldmgr", updates.GetProperty("outdated")[0].GetProperty("id").GetString());
        Assert.Equal("Payload Manager", updates.GetProperty("outdated")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_check_without_a_manifest_is_unavailable()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"{Base}/updates/check", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("manifest", body.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Applying_boot_settings_without_a_manifest_is_unavailable()
    {
        await using var factory = new CompanionFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"{Base}/boot-settings/apply", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Applying_boot_settings_while_a_model_loads_is_a_conflict()
    {
        using var harness = new SetupHarness();
        await using var factory = new CompanionFactory
        {
            Settings = new Dictionary<string, string> { ["PS5:Consoles:ps5:UpdateCheckIntervalMinutes"] = "0" },
        };
        factory.Connectors["ps5"].UpdateCheck = UpdateServices(harness);
        factory.Connectors["ps5"].Manifest = harness.Manifest;
        await factory.WaitForFirstCheckAsync();
        factory.Registry.Find("ps5")!.Store.Publish(CompanionStatus.Initial with
        {
            Lifecycle = CompanionLifecycle.Loading,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync($"{Base}/boot-settings/apply", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("model load", body.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Applying_boot_settings_raises_a_short_countdown()
    {
        using var harness = await PreparedConsoleAsync();
        harness.Autoload = harness.Autoload with { DelaySeconds = 3, DelayReported = true };
        await using var factory = new CompanionFactory
        {
            Settings = new Dictionary<string, string> { ["PS5:Consoles:ps5:UpdateCheckIntervalMinutes"] = "0" },
        };
        factory.Connectors["ps5"].UpdateCheck = UpdateServices(harness);
        factory.Connectors["ps5"].Manifest = harness.Manifest;
        using var client = factory.CreateClient();
        await factory.WaitForFirstCheckAsync();

        using var response = await client.PostAsync($"{Base}/boot-settings/apply", null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(10, harness.Autoload.DelaySeconds);
        Assert.Equal(0, body.GetProperty("settings").GetArrayLength());
    }

    private static SetupServices UpdateServices(SetupHarness harness) => new(
        harness.Manager,
        harness.Artifacts,
        harness.Files,
        harness.Probe,
        harness.Elfldr,
        harness.VoidShell,
        new SetupHarness.FakeUi(harness),
        TimeProvider.System);

    private static async Task<SetupHarness> PreparedConsoleAsync()
    {
        var harness = new SetupHarness();
        harness.VoidShell.SetUp([SetupHarness.TitleId]);
        harness.Files.AddFile("/data/OnionHEN/config.ini", "[general]\nx=1\n[app_jailbreak]\nexact_title_ids = PPSA00001\n");
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        foreach (var payload in harness.Manifest.Payloads)
        {
            var local = await harness.Artifacts.FetchAsync(payload.Url, payload.Sha256, CancellationToken.None);
            var stored = harness.Stored.Single(item => item.FileName == payload.UploadName);
            harness.Files.AddFile(stored.Path!, await File.ReadAllBytesAsync(local));
        }

        harness.Events.Clear();
        return harness;
    }
}

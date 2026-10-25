using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class ConsoleSettingsPageTests
{
    [Fact]
    public async Task The_settings_page_marks_configuration_consoles_and_their_keys()
    {
        await using var factory = new CompanionFactory { ConsoleIds = ["den", "living-room"] };
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/settings/consoles");

        Assert.Contains("Manage consoles", html);
        Assert.Contains("From configuration", html);
        Assert.Contains("PS5__Consoles__den__Host", html);
        Assert.Contains("PS5:Consoles:living-room", html);
        Assert.Contains("cannot contain a hyphen", html);
    }

    [Fact]
    public async Task An_empty_fleet_offers_to_add_the_first_console()
    {
        await using var factory = new CompanionFactory { ConsoleIds = [] };
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("Add your first console", html);
        Assert.Contains("Add manually", html);
        Assert.Contains("Scan network", html);
    }

    [Fact]
    public async Task A_connection_test_reports_each_check_for_unsaved_settings()
    {
        await using var factory = new CompanionFactory { ConsoleIds = [] };
        var fake = factory.Connectors["probe"];
        fake.Payloads.Snapshot = new(true, true, null, [], new HashSet<string>());
        fake.Elfldr.Listening = true;
        fake.Llama.Result = PS5LM.Companion.Core.Abstractions.LlamaProbeResult.Closed;
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/consoles/test", new { host = "10.0.0.8", voidShellPort = 0 });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checks = json.GetProperty("checks").EnumerateArray().ToList();
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "FTP" && check.GetProperty("outcome").GetString() == "Pass");
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "llama-server" && check.GetProperty("outcome").GetString() == "Skipped");
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "VoidShell" && check.GetProperty("outcome").GetString() == "Skipped");

        using var invalid = await client.PostAsJsonAsync("/api/consoles/test", new { host = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}

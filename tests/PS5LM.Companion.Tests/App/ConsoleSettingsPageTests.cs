using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;
using MudBlazor.Services;

using PS5LM.Companion.App.Components;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Core.Transfers;
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
        Assert.Contains("network_mode: host", html);
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

        using var response = await client.PostAsJsonAsync("/api/consoles/test", new { host = "10.0.0.8" });
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var checks = json.GetProperty("checks").EnumerateArray().ToList();
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "FTP" && check.GetProperty("outcome").GetString() == "Pass");
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "llama-server" && check.GetProperty("outcome").GetString() == "Skipped");
        Assert.Contains(checks, check => check.GetProperty("name").GetString() == "VoidShell" && check.GetProperty("outcome").GetString() == "Fail");

        using var disabled = await client.PostAsJsonAsync("/api/consoles/test", new { host = "10.0.0.8", voidShellPort = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);
        Assert.Contains("VoidShell port is required", await disabled.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var invalid = await client.PostAsJsonAsync("/api/consoles/test", new { host = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Discovery_rejects_a_bad_cidr_and_scans_a_single_address()
    {
        await using var factory = new CompanionFactory { ConsoleIds = [] };
        using var client = factory.CreateClient();

        using var bad = await client.PostAsJsonAsync("/api/consoles/discover", new { cidr = "nope" });
        using var none = await client.PostAsJsonAsync("/api/consoles/discover", new { cidr = "192.0.2.1/32" });
        var found = await none.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal(0, found.GetArrayLength());
    }
}

public sealed class ConsoleEditorDialogTests : BunitContext, IAsyncLifetime
{
    public ConsoleEditorDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    /// <summary>MudBlazor services only dispose asynchronously. xUnit also calls <see cref="IDisposable.Dispose"/>.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    [Fact]
    public async Task The_id_can_be_edited_on_an_existing_console()
    {
        using var registry = new ConsoleRegistry(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System);
        Assert.True(registry.Add("den", new PS5Options { Host = "10.0.0.8", DisplayName = "Den" }).Succeeded);
        Services.AddSingleton<IConsoleRegistry>(registry);
        Services.AddSingleton<IConsoleProbe, UnusedProbe>();
        Services.AddSingleton(new ConsoleSettingsService(
            registry,
            new TransferService(new FakeHub(), registry, new MemoryTransferStore(), new TransferOptions(), TimeProvider.System),
            new HybridInferencePools(new InferenceOptions(), registry)));

        var cut = Render<MudDialogProvider>();
        var dialogs = Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<ConsoleEditor> { { editor => editor.ConsoleId, "den" } };
        await cut.InvokeAsync(() => dialogs.ShowAsync<ConsoleEditor>("Edit console", parameters));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Changing it changes the console's addresses and API paths.", cut.Markup, StringComparison.Ordinal);
            var id = cut.FindAll(".mud-input-control").Single(control => control.TextContent.Contains("Changing it changes the console's addresses", StringComparison.Ordinal));
            var input = id.QuerySelector("input");
            Assert.NotNull(input);
            Assert.False(input!.HasAttribute("readonly"));
            Assert.Equal("den", input.GetAttribute("value"));
        });
    }

    private sealed class UnusedProbe : IConsoleProbe
    {
        public Task<ConsoleProbeReport> TestAsync(PS5Options options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

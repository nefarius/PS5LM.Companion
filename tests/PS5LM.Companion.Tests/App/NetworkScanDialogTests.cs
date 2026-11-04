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

public sealed class NetworkScanDialogTests : BunitContext, IAsyncLifetime
{
    private readonly ConsoleRegistry _registry = new(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System);

    public NetworkScanDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IConsoleRegistry>(_registry);
        Services.AddSingleton<IConsoleProbe, UnusedProbe>();
        Services.AddSingleton(new ConsoleSettingsService(
            _registry,
            new TransferService(new FakeHub(), _registry, new MemoryTransferStore(), new TransferOptions(), TimeProvider.System),
            new HybridInferencePools(new InferenceOptions(), _registry)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        _registry.Dispose();
        await DisposeAsync();
    }

    /// <summary>MudBlazor services only dispose asynchronously. xUnit also calls <see cref="IDisposable.Dispose"/>.</summary>
    protected override void Dispose(bool disposing)
    {
    }

    [Fact]
    public async Task Use_hostname_opens_the_editor_and_keeps_the_scan()
    {
        var cut = RenderDialog(WithHostName());
        var pending = Show(cut);
        await ScanAsync(cut);

        Click(cut, "Use hostname");
        cut.WaitForAssertion(() => Assert.Contains("Add a console", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("ps5.lan", HostValue(cut));
        Assert.Contains("10.0.0.5", cut.Markup, StringComparison.Ordinal);

        Click(cut, "Cancel");
        cut.WaitForAssertion(() => Assert.DoesNotContain("Add a console", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Use IP", cut.Markup, StringComparison.Ordinal);
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task Use_ip_opens_the_editor_with_the_address()
    {
        var cut = RenderDialog(WithHostName());
        _ = Show(cut);
        await ScanAsync(cut);

        Click(cut, "Use IP");
        cut.WaitForAssertion(() => Assert.Contains("Add a console", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("10.0.0.5", HostValue(cut));
    }

    [Fact]
    public async Task Without_a_hostname_only_the_address_is_offered()
    {
        var console = new DiscoveredConsole("10.0.0.6", true, false, false, false, false);
        var cut = RenderDialog(console);
        _ = Show(cut);
        await ScanAsync(cut);

        Assert.DoesNotContain("Use hostname", cut.Markup, StringComparison.Ordinal);
        Click(cut, "Use IP");
        cut.WaitForAssertion(() => Assert.Contains("Add a console", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("10.0.0.6", HostValue(cut));
    }

    [Fact]
    public async Task Adding_a_console_marks_it_without_dismissing_the_scan()
    {
        var cut = RenderDialog(WithHostName(), new DiscoveredConsole("10.0.0.6", true, false, false, false, false));
        var pending = Show(cut);
        await ScanAsync(cut);

        Click(cut, "Use hostname");
        cut.WaitForAssertion(() => Assert.Contains("Add a console", cut.Markup, StringComparison.Ordinal));
        SetText(cut, "Name", "Den");
        Click(cut, "Save");

        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Add a console", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Already added", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("ps5.lan", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("10.0.0.6", cut.Markup, StringComparison.Ordinal);
        });
        Assert.False(pending.IsCompleted);
        Assert.Contains(_registry.All, console => console.Host == "ps5.lan");

        Click(cut, "Use IP");
        cut.WaitForAssertion(() => Assert.Contains("Add a console", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("10.0.0.6", HostValue(cut));
    }

    [Fact]
    public async Task A_console_added_elsewhere_is_marked_on_the_open_scan()
    {
        var cut = RenderDialog(WithHostName());
        var pending = Show(cut);
        await ScanAsync(cut);

        Assert.True(_registry.Add("den", new PS5Options { Host = "10.0.0.5" }).Succeeded);

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Already added", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Use hostname", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Use IP", cut.Markup, StringComparison.Ordinal);
        });
        Assert.False(pending.IsCompleted);
    }

    [Fact]
    public async Task A_configured_host_or_hostname_is_already_added()
    {
        Assert.True(_registry.Add("den", new PS5Options { Host = "PS5.LAN" }).Succeeded);
        Assert.True(_registry.Add("living-room", new PS5Options { Host = "10.0.0.6" }).Succeeded);
        var cut = RenderDialog(WithHostName(), new DiscoveredConsole("10.0.0.6", true, false, false, false, false));
        _ = Show(cut);
        await ScanAsync(cut, "Already added");

        Assert.Equal(2, cut.FindAll(".mud-chip").Count(chip => chip.TextContent.Contains("Already added", StringComparison.Ordinal)));
        Assert.DoesNotContain("Use hostname", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Use IP", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Voidshell_fallback_flags_are_shown()
    {
        var cut = RenderDialog(WithHostName());
        var pending = Show(cut);
        await ScanAsync(cut);

        Assert.Contains("ps5.lan", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("10.0.0.5", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("no PS5LM app installed", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("found via VoidShell (FTP unavailable)", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("not the nefarius fork", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Payload Manager", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("VoidShell", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("ps5-exporter", cut.Markup, StringComparison.Ordinal);

        Click(cut, "Close");
        await pending;
        Assert.DoesNotContain("Scan network", cut.Markup, StringComparison.Ordinal);
    }

    private IRenderedComponent<MudDialogProvider> RenderDialog(params DiscoveredConsole[] consoles)
    {
        Services.AddSingleton<IConsoleDiscovery>(new StubDiscovery(consoles));
        return Render<MudDialogProvider>();
    }

    private static Task Show(IRenderedComponent<MudDialogProvider> cut)
    {
        var dialogs = cut.Services.GetRequiredService<IDialogService>();
        var pending = cut.InvokeAsync(() => NetworkScanDialog.ShowAsync(dialogs));
        cut.WaitForAssertion(() => Assert.Contains("Scan network", cut.Markup, StringComparison.Ordinal));
        return pending;
    }

    private static async Task ScanAsync(IRenderedComponent<MudDialogProvider> cut, string until = "Use IP")
    {
        cut.FindAll("button").Single(button => button.TextContent == "Scan").Click();
        cut.WaitForAssertion(() => Assert.Contains(until, cut.Markup, StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    private static void Click(IRenderedComponent<MudDialogProvider> cut, string label) =>
        cut.FindAll("button").Single(button => button.TextContent.Contains(label, StringComparison.Ordinal)).Click();

    private static void SetText(IRenderedComponent<MudDialogProvider> cut, string label, string value)
    {
        var control = cut.FindAll(".mud-input-control").Single(item => item.TextContent.Contains(label, StringComparison.Ordinal));
        var input = control.QuerySelector("input") ?? throw new InvalidOperationException($"No input for {label}.");
        input.Change(value);
    }

    private static string? HostValue(IRenderedComponent<MudDialogProvider> cut)
    {
        var host = cut.FindAll(".mud-input-control").Single(control =>
            control.TextContent.Contains("Host", StringComparison.Ordinal)
            && !control.TextContent.Contains("CIDR", StringComparison.Ordinal));
        return host.QuerySelector("input")?.GetAttribute("value");
    }

    private static DiscoveredConsole WithHostName() => new(
        "10.0.0.5",
        false,
        false,
        true,
        true,
        true,
        "ps5.lan",
        Ftp: false,
        VoidShellFork: false);

    private sealed class StubDiscovery(DiscoveredConsole[] consoles) : IConsoleDiscovery
    {
        public Task<IReadOnlyList<DiscoveredConsole>> ScanAsync(string? cidr, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveredConsole>>(consoles);
    }

    private sealed class UnusedProbe : IConsoleProbe
    {
        public Task<ConsoleProbeReport> TestAsync(PS5Options options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

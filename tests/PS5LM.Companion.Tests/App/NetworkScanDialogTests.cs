using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;
using MudBlazor.Services;

using PS5LM.Companion.App.Components;
using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.Tests.App;

public sealed class NetworkScanDialogTests : BunitContext, IAsyncLifetime
{
    public NetworkScanDialogTests()
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
    public async Task Use_hostname_returns_the_resolved_name()
    {
        var host = await ChooseAsync(WithHostName(), "Use hostname");

        Assert.Equal("ps5.lan", host);
    }

    [Fact]
    public async Task Use_ip_returns_the_address_when_a_hostname_was_also_offered()
    {
        var host = await ChooseAsync(WithHostName(), "Use IP");

        Assert.Equal("10.0.0.5", host);
    }

    [Fact]
    public async Task Without_a_hostname_only_the_address_is_offered()
    {
        var console = new DiscoveredConsole("10.0.0.6", true, false, false, false, false);
        var cut = RenderDialog(console);
        var pending = Show(cut);
        await ScanAsync(cut);

        Assert.DoesNotContain("Use hostname", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("10.0.0.6", await ClickAsync(cut, pending, "Use IP"));
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

        cut.FindAll("button").Single(button => button.TextContent.Contains("Cancel", StringComparison.Ordinal)).Click();
        Assert.Null(await pending);
    }

    private async Task<string?> ChooseAsync(DiscoveredConsole console, string label)
    {
        var cut = RenderDialog(console);
        var pending = Show(cut);
        await ScanAsync(cut);
        return await ClickAsync(cut, pending, label);
    }

    private IRenderedComponent<MudDialogProvider> RenderDialog(DiscoveredConsole console)
    {
        Services.AddSingleton<IConsoleDiscovery>(new StubDiscovery(console));
        return Render<MudDialogProvider>();
    }

    private static Task<string?> Show(IRenderedComponent<MudDialogProvider> cut)
    {
        var dialogs = cut.Services.GetRequiredService<IDialogService>();
        var pending = cut.InvokeAsync(() => NetworkScanDialog.ShowAsync(dialogs));
        cut.WaitForAssertion(() => Assert.Contains("Scan network", cut.Markup, StringComparison.Ordinal));
        return pending;
    }

    private static async Task ScanAsync(IRenderedComponent<MudDialogProvider> cut)
    {
        cut.FindAll("button").Single(button => button.TextContent == "Scan").Click();
        cut.WaitForAssertion(() => Assert.Contains("Use IP", cut.Markup, StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    private static Task<string?> ClickAsync(IRenderedComponent<MudDialogProvider> cut, Task<string?> pending, string label)
    {
        cut.FindAll("button").Single(button => button.TextContent.Contains(label, StringComparison.Ordinal)).Click();
        return pending;
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

    private sealed class StubDiscovery(DiscoveredConsole console) : IConsoleDiscovery
    {
        public Task<IReadOnlyList<DiscoveredConsole>> ScanAsync(string? cidr, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveredConsole>>([console]);
    }
}

using System.Net;
using System.Text;

using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using PS5LM.Companion.App.Updates;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class ConsoleNavigationActionTests
{
    [Fact]
    public async Task Each_console_menu_offers_its_own_actions()
    {
        await using var ctx = Context();
        var registry = new FixedRegistry(
            NavConsole("den", "Den"),
            NavConsole("living-room", "Living room", ConsoleSource.Configuration));
        Register(ctx, registry, Idle(registry));
        var cut = ctx.Render<PS5LM.Companion.App.Components.Layout.MainLayout>();

        await Open(cut, "Den");
        Assert.Contains("Test connection", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Copy models from this console...", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Edit", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Delete", cut.Markup, StringComparison.Ordinal);

        await Open(cut, "Living room");
        cut.WaitForAssertion(() =>
        {
            var menus = cut.FindAll(".mud-popover-open")
                .Select(popover => string.Join('\n', popover.QuerySelectorAll(".mud-menu-item").Select(item => item.TextContent)))
                .Where(text => text.Contains("Test connection", StringComparison.Ordinal))
                .ToList();
            Assert.Contains(menus, text => text.Contains("Edit", StringComparison.Ordinal));
            Assert.Contains(menus, text => !text.Contains("Edit", StringComparison.Ordinal) && !text.Contains("Delete", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Delete_asks_for_confirmation_and_can_be_cancelled()
    {
        await using var ctx = Context();
        var registry = new RemovableRegistry(NavConsole("den", "Den"), NavConsole("living-room", "Living room"));
        Register(ctx, registry, Idle(registry));
        var cut = ctx.Render<PS5LM.Companion.App.Components.Layout.MainLayout>();

        await Open(cut, "Den");
        Item(cut, "Delete").Click();

        cut.WaitForAssertion(() => Assert.Contains("Den (10.0.0.8)", cut.Markup, StringComparison.Ordinal));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        Assert.Equal(0, registry.Removals);
        Assert.Contains(registry.All, console => console.Id == "den");
    }

    [Fact]
    public async Task Delete_is_refused_while_a_transfer_is_active()
    {
        await using var ctx = Context();
        var registry = new RemovableRegistry(NavConsole("den", "Den"), NavConsole("living-room", "Living room"));
        var transfers = new TransferService(new FakeHub(), registry, new MemoryTransferStore
        {
            Saved =
            [
                new Transfer(
                    Guid.NewGuid(), "den", "acme/tiny", "m.gguf", null, 1, "Internal storage", TransferState.Queued,
                    0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
        }, new TransferOptions(), TimeProvider.System);
        await transfers.InitializeAsync(CancellationToken.None);
        Register(ctx, registry, transfers);
        var cut = ctx.Render<PS5LM.Companion.App.Components.Layout.MainLayout>();

        await Open(cut, "Den");
        Item(cut, "Delete").Click();

        cut.WaitForAssertion(() => Assert.Contains("Cannot remove Den", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("m.gguf", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(0, registry.Removals);
    }

    [Fact]
    public async Task Copy_from_the_drawer_names_that_console()
    {
        await using var ctx = Context();
        var registry = new FixedRegistry(NavConsole("den", "Den"), NavConsole("living-room", "Living room"));
        Register(ctx, registry, Idle(registry));
        var cut = ctx.Render<PS5LM.Companion.App.Components.Layout.MainLayout>();

        await Open(cut, "Living room");
        Item(cut, "Copy models from this console...").Click();

        cut.WaitForAssertion(() => Assert.Contains("Living room", cut.Find(".mud-dialog").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Renaming_from_the_drawer_keeps_the_open_console_address()
    {
        await using var ctx = Context();
        var store = new MemoryConsoleStore(
        [
            new StoredConsole("den", new PS5Options { Host = "10.0.0.8", DisplayName = "Den" }),
            new StoredConsole("living-room", new PS5Options { Host = "10.0.0.9", DisplayName = "Living room" }),
        ]);
        var registry = new ConsoleRegistry(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System, store);
        var transfers = Idle(registry);
        ctx.Services.AddSingleton<IConsoleRegistry>(registry);
        ctx.Services.AddSingleton(transfers);
        ctx.Services.AddSingleton(new ConsoleSettingsService(registry, transfers, new EmptyPools()));
        ctx.Services.AddSingleton<IConsoleProbe>(new UnusedProbe());
        ctx.Services.AddMudServices();
        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/consoles/den/models");
        var cut = ctx.Render<PS5LM.Companion.App.Components.Layout.MainLayout>();

        await Open(cut, "Den");
        Item(cut, "Edit").Click();
        cut.WaitForAssertion(() => Assert.Contains("Edit console", cut.Markup, StringComparison.Ordinal));
        var id = cut.FindAll(".mud-input-control").Single(control => control.TextContent.Contains("Changing it changes the console's addresses", StringComparison.Ordinal));
        id.QuerySelector("input")!.Input("den-2");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        cut.WaitForAssertion(() => Assert.Contains("/consoles/den-2/models", navigation.Uri, StringComparison.Ordinal));
        Assert.NotNull(registry.Find("den-2"));
    }

    private static BunitContext Context()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var ctx = new BunitContext { JSInterop = { Mode = JSRuntimeMode.Loose } };
        ctx.Services.AddSingleton(new LatestReleaseChecker(new HttpClient(handler, disposeHandler: false), new FixedVersion()));
        return ctx;
    }

    private static void Register(BunitContext ctx, IConsoleRegistry registry, TransferService transfers)
    {
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton(transfers);
        ctx.Services.AddSingleton<IConsoleProbe>(new UnusedProbe());
        ctx.Services.AddMudServices();
    }

    private static TransferService Idle(IConsoleRegistry registry) =>
        new(new FakeHub(), registry, new MemoryTransferStore(), new TransferOptions(), TimeProvider.System);

    private static ConsoleRuntime NavConsole(string id, string name, ConsoleSource source = ConsoleSource.User) =>
        new(id, new PS5Options { Host = "10.0.0.8", DisplayName = name }, new FakeConsole().ToConnectors(), TimeProvider.System, source);

    private static async Task Open(IRenderedComponent<PS5LM.Companion.App.Components.Layout.MainLayout> cut, string name)
    {
        var activator = cut.FindAll(".mud-menu-activator > div").Single(item => item.TextContent.Contains(name, StringComparison.Ordinal));
        await activator.TriggerEventAsync("oncontextmenu", new MouseEventArgs { Button = 2, ClientX = 12, ClientY = 24 });
        cut.WaitForAssertion(() => Assert.Contains("Test connection", activator.TextContent.Contains("Manage", StringComparison.Ordinal) ? string.Empty : cut.Markup, StringComparison.Ordinal));
    }

    private static AngleSharp.Dom.IElement Item(IRenderedComponent<PS5LM.Companion.App.Components.Layout.MainLayout> cut, string label) =>
        cut.FindAll(".mud-menu-item").Single(item => item.TextContent.Contains(label, StringComparison.Ordinal));

    private sealed class FixedVersion : IAssemblyVersionSource
    {
        public string? TryGetVersion() => "1.0.0";
    }

    private sealed class UnusedProbe : IConsoleProbe
    {
        public Task<ConsoleProbeReport> TestAsync(PS5Options options, CancellationToken cancellationToken) =>
            Task.FromResult(new ConsoleProbeReport([]));
    }

    private sealed class EmptyPools : IInferencePoolSource
    {
        public IReadOnlyList<InferencePool> All { get; } = [];

        public event Action? Changed { add { } remove { } }

        public InferencePool? Find(string? alias) => null;

        public InferencePool? Resolve(string? model) => null;

        public PoolEditResult Add(string alias, string model, IReadOnlyList<string> consoles, IReadOnlyList<string>? alternateNames = null) =>
            throw new NotSupportedException();

        public PoolEditResult Update(string alias, string model, IReadOnlyList<string> consoles, string? newAlias = null, IReadOnlyList<string>? alternateNames = null) =>
            throw new NotSupportedException();

        public PoolEditResult Remove(string alias) => throw new NotSupportedException();
    }

    private sealed class MemoryConsoleStore(IReadOnlyList<StoredConsole> consoles) : IConsoleStore
    {
        public IReadOnlyList<StoredConsole> Load() => consoles;

        public void Save(IReadOnlyList<StoredConsole> saved)
        {
        }
    }

    private sealed class FixedRegistry(params ConsoleRuntime[] consoles) : IConsoleRegistry
    {
        public IReadOnlyList<ConsoleRuntime> All { get; } = consoles;

        public event Action<ConsoleChange>? Changed { add { } remove { } }

        public ConsoleRuntime? Find(string? id) =>
            consoles.FirstOrDefault(console => string.Equals(console.Id, id, StringComparison.OrdinalIgnoreCase));

        public ConsoleEditResult Add(string id, PS5Options options) => throw new NotSupportedException();

        public ConsoleEditResult Update(string id, PS5Options options, string? newId = null) => throw new NotSupportedException();

        public ConsoleEditResult Remove(string id) => throw new NotSupportedException();
    }

    private sealed class RemovableRegistry(params ConsoleRuntime[] consoles) : IConsoleRegistry
    {
        private readonly List<ConsoleRuntime> _consoles = [.. consoles];

        public int Removals { get; private set; }

        public IReadOnlyList<ConsoleRuntime> All => _consoles;

        public event Action<ConsoleChange>? Changed;

        public ConsoleRuntime? Find(string? id) =>
            _consoles.FirstOrDefault(console => string.Equals(console.Id, id, StringComparison.OrdinalIgnoreCase));

        public ConsoleEditResult Add(string id, PS5Options options) => throw new NotSupportedException();

        public ConsoleEditResult Update(string id, PS5Options options, string? newId = null) => throw new NotSupportedException();

        public ConsoleEditResult Remove(string id)
        {
            var console = Find(id) ?? throw new InvalidOperationException(id);
            _consoles.Remove(console);
            Removals++;
            Changed?.Invoke(new ConsoleChange(ConsoleChangeKind.Removed, console, null));
            return new ConsoleEditResult(ConsoleEditStatus.Ok, null, console);
        }
    }
}

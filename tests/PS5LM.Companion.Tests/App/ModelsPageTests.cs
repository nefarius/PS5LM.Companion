using System.Text.Json.Nodes;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using PS5LM.Companion.App.Components.Pages;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class ModelsPageTests
{
    [Fact]
    public async Task The_models_page_turns_auto_load_on_for_the_chosen_model()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/a.gguf", new byte[1])
            .AddFile(
                "/data/PS5LM/settings.json",
                """{"auto_load":false,"default_model":"/data/PS5LM/models/a.gguf","sounds":false}""");
        var runtime = new ConsoleRuntime(
            "ps5",
            new PS5Options { Host = "ps5", DisplayName = "Den" },
            new FakeConsole(fs).ToConnectors(),
            TimeProvider.System,
            ConsoleSource.User);
        runtime.Store.Publish(await runtime.Status.RefreshAsync(CancellationToken.None));
        var registry = new PageRegistry(runtime);
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IConsoleRegistry>(registry);
        ctx.Services.AddSingleton(new TransferService(new FakeHub(), registry, new MemoryTransferStore(), new TransferOptions(), TimeProvider.System));
        ctx.Services.AddSingleton(sp => new ModelDeletionService(sp.GetRequiredService<TransferService>()));
        ctx.Services.AddMudServices();

        var cut = ctx.Render<Models>(p => p.Add(c => c.ConsoleId, "ps5"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Load a model when the app starts", cut.Markup, StringComparison.Ordinal);
            Assert.Contains("Applies the next time the app starts", cut.Markup, StringComparison.Ordinal);
            Assert.Contains(">Default<", cut.Markup, StringComparison.Ordinal);
        });

        var toggle = cut.Find(".mud-switch input");
        Assert.False(toggle.HasAttribute("checked"));
        await cut.InvokeAsync(() => toggle.Change(true));

        cut.WaitForAssertion(() =>
        {
            var json = JsonNode.Parse(fs.Text("/data/PS5LM/settings.json")!)!;
            Assert.True(json["auto_load"]!.GetValue<bool>());
            Assert.Equal("/data/PS5LM/models/a.gguf", json["default_model"]!.GetValue<string>());
            Assert.False(json["sounds"]!.GetValue<bool>());
        });
    }

    private sealed class PageRegistry(ConsoleRuntime console) : IConsoleRegistry
    {
        public IReadOnlyList<ConsoleRuntime> All { get; } = [console];

        public event Action<ConsoleChange>? Changed { add { } remove { } }

        public ConsoleRuntime? Find(string? id) =>
            string.Equals(id, console.Id, StringComparison.OrdinalIgnoreCase) ? console : null;

        public ConsoleEditResult Add(string id, PS5Options options) => throw new NotSupportedException();

        public ConsoleEditResult Update(string id, PS5Options options, string? newId = null) => throw new NotSupportedException();

        public ConsoleEditResult Remove(string id) => throw new NotSupportedException();
    }
}

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using PS5LM.Companion.App.Components.Pages;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class DownloadsPageTests
{
    [Fact]
    public async Task A_running_search_disables_the_button_and_shows_progress()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<HubRepo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var page = await OpenAsync(new ScriptedHub { Search = _ => gate.Task });

        Assert.True(SearchButton(page).HasAttribute("disabled"));

        TypeQuery(page, "llama");
        Assert.False(SearchButton(page).HasAttribute("disabled"));

        var pending = SearchButton(page).ClickAsync();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Searching...", page.Markup, StringComparison.Ordinal);
            Assert.Contains("mud-progress-linear", page.Markup, StringComparison.Ordinal);
            Assert.True(SearchButton(page).HasAttribute("disabled"));
        });

        gate.SetResult([new HubRepo("acme/llama", 10)]);
        await pending;

        page.WaitForAssertion(() =>
        {
            Assert.Contains("acme/llama", page.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Searching...", page.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("mud-progress-linear", page.Markup, StringComparison.Ordinal);
            Assert.False(SearchButton(page).HasAttribute("disabled"));
        });
    }

    [Fact]
    public async Task A_search_with_no_repositories_says_nothing_matched()
    {
        await using var page = await OpenAsync(new ScriptedHub());

        TypeQuery(page, "nope");
        await SearchButton(page).ClickAsync();

        page.WaitForAssertion(() =>
            Assert.Contains("No GGUF repositories match \"nope\".", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Searching...", page.Markup, StringComparison.Ordinal);
        Assert.False(SearchButton(page).HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_hub_error_is_shown_and_search_can_be_tried_again()
    {
        await using var page = await OpenAsync(new ScriptedHub
        {
            Search = _ => throw new HubUnavailableException("The model hub did not answer: timeout"),
        });

        TypeQuery(page, "llama");
        await SearchButton(page).ClickAsync();

        page.WaitForAssertion(() =>
            Assert.Contains("The model hub did not answer: timeout", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("No GGUF repositories match", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Searching...", page.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("mud-progress-linear", page.Markup, StringComparison.Ordinal);
        Assert.False(SearchButton(page).HasAttribute("disabled"));
    }

    [Fact]
    public async Task A_search_keeps_the_query_that_was_sent()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<HubRepo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? sent = null;
        await using var page = await OpenAsync(new ScriptedHub
        {
            Search = query =>
            {
                sent = query;
                return gate.Task;
            },
        });

        TypeQuery(page, "nope");
        var pending = SearchButton(page).ClickAsync();
        page.WaitForAssertion(() => Assert.Contains("Searching...", page.Markup, StringComparison.Ordinal));

        TypeQuery(page, "changed");
        gate.SetResult([]);
        await pending;

        Assert.Equal("nope", sent);
        page.WaitForAssertion(() =>
            Assert.Contains("No GGUF repositories match \"nope\".", page.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("No GGUF repositories match \"changed\".", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Results_stay_disabled_until_the_search_and_its_file_list_finish()
    {
        var search = new TaskCompletionSource<IReadOnlyList<HubRepo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new TaskCompletionSource<IReadOnlyList<HubFile>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var page = await OpenAsync(new ScriptedHub
        {
            Search = _ => Interlocked.Increment(ref calls) == 1
                ? Task.FromResult<IReadOnlyList<HubRepo>>([new HubRepo("acme/llama", 10)])
                : search.Task,
            Files = _ => files.Task,
        });

        TypeQuery(page, "llama");
        await SearchButton(page).ClickAsync();
        page.WaitForAssertion(() => Assert.Contains("acme/llama", page.Markup, StringComparison.Ordinal));
        Assert.Empty(page.Cut.FindAll(".mud-list-item-disabled"));

        var pending = SearchButton(page).ClickAsync();
        page.WaitForAssertion(() => Assert.NotEmpty(page.Cut.FindAll(".mud-list-item-disabled")));
        search.SetResult([new HubRepo("acme/llama", 10)]);
        await pending;

        page.WaitForAssertion(() => Assert.Empty(page.Cut.FindAll(".mud-list-item-disabled")));
        var loading = page.Cut.Find(".mud-list-item").ClickAsync();
        page.WaitForAssertion(() =>
        {
            Assert.Contains("Loading files of acme/llama...", page.Markup, StringComparison.Ordinal);
            Assert.True(SearchButton(page).HasAttribute("disabled"));
            Assert.NotEmpty(page.Cut.FindAll(".mud-list-item-disabled"));
        });
        files.SetResult([new HubFile("model.gguf", 8, null)]);
        await loading;

        page.WaitForAssertion(() =>
        {
            Assert.Contains("model.gguf", page.Markup, StringComparison.Ordinal);
            Assert.False(SearchButton(page).HasAttribute("disabled"));
        });
    }

    private static Task<OpenedPage> OpenAsync(ScriptedHub hub)
    {
        var runtime = new ConsoleRuntime(
            "ps5",
            new PS5Options { Host = "ps5", DisplayName = "Den" },
            new FakeConsole(ConsoleFixtures.PS5LMInstalled()).ToConnectors(),
            TimeProvider.System,
            ConsoleSource.User);
        var registry = new PageRegistry(runtime);
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IConsoleRegistry>(registry);
        ctx.Services.AddSingleton<IModelHub>(hub);
        ctx.Services.AddSingleton(new TransferService(hub, registry, new MemoryTransferStore(), new TransferOptions(), TimeProvider.System));
        ctx.Services.AddMudServices();

        var cut = ctx.Render<Transfers>(p => p.Add(c => c.ConsoleId, "ps5"));
        cut.WaitForAssertion(() => Assert.Contains("Find a model on Hugging Face", cut.Markup, StringComparison.Ordinal));
        return Task.FromResult(new OpenedPage(ctx, cut));
    }

    private static AngleSharp.Dom.IElement SearchButton(OpenedPage page) =>
        page.Cut.FindAll("button").Single(button => button.TextContent.Contains("Search", StringComparison.Ordinal));

    private static void TypeQuery(OpenedPage page, string query)
    {
        var field = page.Cut.FindAll(".mud-input-control").Single(item =>
            item.TextContent.Contains("Search GGUF repositories", StringComparison.Ordinal));
        var input = field.QuerySelector("input") ?? throw new InvalidOperationException("Search field has no input.");
        input.Input(query);
    }

    private sealed class OpenedPage(BunitContext context, IRenderedComponent<Transfers> cut) : IAsyncDisposable
    {
        public IRenderedComponent<Transfers> Cut { get; } = cut;

        public string Markup => Cut.Markup;

        public void WaitForAssertion(Action assertion) => Cut.WaitForAssertion(assertion);

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class ScriptedHub : IModelHub
    {
        public Func<string, Task<IReadOnlyList<HubRepo>>> Search { get; init; } =
            _ => Task.FromResult<IReadOnlyList<HubRepo>>([]);

        public Func<string, Task<IReadOnlyList<HubFile>>> Files { get; init; } =
            _ => Task.FromResult<IReadOnlyList<HubFile>>([]);

        public Task<IReadOnlyList<HubRepo>> SearchAsync(string query, CancellationToken cancellationToken) => Search(query);

        public Task<IReadOnlyList<HubFile>> ListFilesAsync(string repo, CancellationToken cancellationToken) => Files(repo);

        public Task DownloadAsync(
            string repo,
            string fileName,
            string destinationPath,
            IProgress<long>? progress,
            CancellationToken cancellationToken) => Task.CompletedTask;
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

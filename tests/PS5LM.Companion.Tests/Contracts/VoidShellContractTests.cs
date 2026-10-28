using System.Net;
using System.Text;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Infrastructure.Setup;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Contracts;

/// <summary>VoidShellClient and the rescan probe against the recorded V1.1 responses.</summary>
public sealed class VoidShellContractTests
{
    [Fact]
    public async Task Library_lists_the_recorded_title_and_an_empty_library_lists_none()
    {
        var listed = await Probe("library.json", "stats.json");
        var empty = await Probe("library-empty.json", "stats.json");

        Assert.Equal(["PPSA99575"], listed.InstalledTitleIds);
        Assert.Empty(empty.InstalledTitleIds);
        Assert.Equal("V1.1", listed.Version);
    }

    [Fact]
    public async Task An_eight_character_foreground_title_still_matches_the_variant()
    {
        var snapshot = await Probe("library.json", "stats-prefix.json");
        Assert.Equal("PPSA9957", snapshot.ActiveTitleId);

        var status = AppControlEvaluator.Evaluate([snapshot]);
        var ps5lm = Assert.Single(status.Apps, app => app.TitleId == "PPSA99575");
        Assert.Equal(AppRunState.Running, ps5lm.State);
    }

    [Fact]
    public async Task The_fork_running_app_wins_over_home_screen_focus()
    {
        var snapshot = await Probe("library.json", "stats-home-focus.json", "V1.1.1-nefarius.2");
        Assert.Equal("PPSA99575", snapshot.ActiveTitleId);

        var ps5lm = Assert.Single(AppControlEvaluator.Evaluate([snapshot]).Apps, app => app.TitleId == "PPSA99575");
        Assert.Equal(AppRunState.Running, ps5lm.State);
    }

    [Fact]
    public async Task A_stale_active_game_with_menu_state_does_not_hide_the_running_app()
    {
        var snapshot = await Probe("library.json", "stats-menu.json", "V1.1.1-nefarius.2");

        Assert.Equal("PPSA99575", snapshot.ActiveTitleId);
        var ps5lm = Assert.Single(AppControlEvaluator.Evaluate([snapshot]).Apps, app => app.TitleId == "PPSA99575");
        Assert.Equal(AppRunState.Running, ps5lm.State);
    }

    [Fact]
    public async Task An_empty_running_app_means_nothing_runs_even_with_focus_on_a_title()
    {
        var snapshot = await Probe("library.json", "stats-idle.json", "V1.1.1-nefarius.2");

        Assert.True(snapshot.RunningKnown);
        Assert.True(string.IsNullOrEmpty(snapshot.ActiveTitleId));
        Assert.All(AppControlEvaluator.Evaluate([snapshot]).Apps, app => Assert.Equal(AppRunState.NotRunning, app.State));
    }

    [Fact]
    public async Task Rescan_fixture_is_a_successful_empty_post()
    {
        var http = new HttpClient(new RescanHandler());
        var probe = new ConsoleProbe("voidshell.test", http);

        Assert.True(await probe.PostAsync(7007, "/api/rescan", CancellationToken.None));
        Assert.Equal("{\"status\":\"ok\"}", RescanHandler.Body.Trim());
    }

    private static async Task<PS5LM.Companion.Core.Abstractions.AppProviderSnapshot> Probe(string library, string stats, string version = "V1.1")
    {
        var http = new HttpClient(new LibraryHandler(library, stats, version)) { BaseAddress = new Uri("http://voidshell.test/") };
        return await new VoidShellClient(http).ProbeAsync(CancellationToken.None);
    }

    private sealed class LibraryHandler(string library, string stats, string version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.AbsolutePath switch
            {
                "/api/version" => "version.json",
                "/api/library" => library,
                "/api/stats" => stats,
                _ => null,
            };
            var body = name is null ? "{}" : FixtureFiles.Read("voidshell", version, name);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class RescanHandler : HttpMessageHandler
    {
        public static string Body => FixtureFiles.Read("voidshell", "V1.1", "rescan.json");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/api/rescan", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}

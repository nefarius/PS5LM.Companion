using System.Net;
using System.Text;

using Polly.Timeout;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class AppControlClientTests
{
    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
    {
        handler = new StubHandler(respond);
        return new HttpClient(handler) { BaseAddress = new Uri("http://ps5:7007/") };
    }

    private static HttpResponseMessage VoidShell(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/api/version" => Json("""{"version":"V1.1"}"""),
        "/api/library" => Json("""{"games":[{"id":"PPSA01325","exists":true},{"id":"PPSA99575","exists":true},{"id":"PPSA99581","exists":false}]}"""),
        "/api/stats" => Json("""{"state":"GAME","active_game":"PPSA9957","version":"V1.1"}"""),
        "/api/launch" => Json("""{"status":"ok"}"""),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task VoidShell_probe_reads_library_and_foreground_title()
    {
        var snapshot = await new VoidShellClient(Client(VoidShell, out _)).ProbeAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Equal("V1.1", snapshot.Version);
        Assert.Equal(["PPSA01325", "PPSA99575"], snapshot.InstalledTitleIds);
        Assert.True(snapshot.RunningKnown);
        Assert.Equal("PPSA9957", snapshot.ActiveTitleId);
    }

    [Fact]
    public async Task VoidShell_keeps_a_stale_active_game_out_of_the_foreground_when_the_state_is_menu()
    {
        var client = new VoidShellClient(Client(
            r => r.RequestUri!.AbsolutePath == "/api/stats" ? Json("""{"state":"MENU","active_game":"PPSA9957"}""") : VoidShell(r),
            out _));

        var snapshot = await client.ProbeAsync(CancellationToken.None);

        Assert.True(snapshot.RunningKnown);
        Assert.Null(snapshot.ActiveTitleId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("""{"version":5}""")]
    public async Task VoidShell_with_unexpected_json_shapes_is_unavailable(string body)
    {
        var snapshot = await new VoidShellClient(Client(_ => Json(body), out _)).ProbeAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
    }

    [Fact]
    public async Task Malformed_library_entries_are_skipped()
    {
        var client = new VoidShellClient(Client(
            r => r.RequestUri!.AbsolutePath == "/api/library" ? Json("""{"games":[1,"x",null,{"id":"PPSA99575"}]}""") : VoidShell(r),
            out _));

        var snapshot = await client.ProbeAsync(CancellationToken.None);

        Assert.Equal(["PPSA99575"], snapshot.InstalledTitleIds);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"games":[]}""")]
    [InlineData(HttpStatusCode.OK, "{")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, """{"games":{}}""")]
    public async Task A_library_that_cannot_be_read_is_unavailable(HttpStatusCode code, string body)
    {
        var client = new VoidShellClient(Client(
            r => r.RequestUri!.AbsolutePath == "/api/library" ? Json(body, code) : VoidShell(r),
            out _));

        var snapshot = await client.ProbeAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Contains("library", snapshot.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_library_stays_available()
    {
        var client = new VoidShellClient(Client(
            r => r.RequestUri!.AbsolutePath == "/api/library" ? Json("""{"games":[]}""") : VoidShell(r),
            out _));

        var snapshot = await client.ProbeAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Empty(snapshot.InstalledTitleIds);
    }

    [Fact]
    public async Task Websrv_with_unexpected_json_shapes_is_tolerated()
    {
        var notWebsrv = await new WebsrvClient(Client(_ => Json("[]"), out _)).ProbeAsync(CancellationToken.None);
        var client = new WebsrvClient(Client(
            r => r.RequestUri!.AbsolutePath == "/fs/user/app/" ? Json("""[1,null,"x",{"name":"PPSA99581"}]""") : Websrv(r),
            out _));

        var snapshot = await client.ProbeAsync(CancellationToken.None);

        Assert.False(notWebsrv.Available);
        Assert.Equal(["PPSA99581"], snapshot.InstalledTitleIds);
    }

    [Fact]
    public async Task VoidShell_launch_posts_the_title_id_as_text()
    {
        var client = new VoidShellClient(Client(VoidShell, out var handler));

        var result = await client.LaunchAsync("PPSA99575", CancellationToken.None);

        Assert.True(result.Accepted);
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/launch", request.RequestUri!.AbsolutePath);
        Assert.Equal("text/plain", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task VoidShell_lists_processes_and_skips_malformed_entries()
    {
        var client = new VoidShellClient(Client(
            _ => Json("""{"processes":[{"pid":77,"name":"pldmgr_v0.6.3.el"},{"pid":"x","name":"bad"},{"name":"nopid"},{"pid":5,"name":"ftpsrv.elf"}]}"""),
            out var handler));

        var processes = await client.ListAsync(CancellationToken.None);

        Assert.Equal([new RemoteProcess(77, "pldmgr_v0.6.3.el"), new RemoteProcess(5, "ftpsrv.elf")], processes);
        Assert.Equal("/api/processes", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task An_older_VoidShell_without_the_process_api_lists_nothing()
    {
        var older = new VoidShellClient(Client(VoidShell, out _));
        var down = new VoidShellClient(Client(_ => throw new HttpRequestException("refused"), out _));

        Assert.Null(await older.ListAsync(CancellationToken.None));
        Assert.Null(await down.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VoidShell_kill_posts_the_pid_and_needs_an_ok_status()
    {
        var accepted = new VoidShellClient(Client(_ => Json("""{"status":"ok"}"""), out var handler));
        var refused = new VoidShellClient(Client(_ => Json("""{"status":"error","error":"refused"}"""), out _));

        Assert.True(await accepted.KillAsync(77, CancellationToken.None));
        Assert.False(await refused.KillAsync(1, CancellationToken.None));
        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/kill", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task VoidShell_that_is_something_else_or_down_is_unavailable()
    {
        var other = await new VoidShellClient(Client(_ => Json("""{"hello":1}"""), out _)).ProbeAsync(CancellationToken.None);
        var down = new VoidShellClient(Client(_ => throw new HttpRequestException("refused"), out _));

        Assert.False(other.Available);
        Assert.False((await down.ProbeAsync(CancellationToken.None)).Available);
        Assert.False((await down.LaunchAsync("PPSA99575", CancellationToken.None)).Dispatched);
    }

    [Fact]
    public async Task VoidShell_timeout_is_unavailable_and_a_cancelled_call_still_propagates()
    {
        var timedOut = new VoidShellClient(Client(_ => throw new TimeoutRejectedException("The operation didn't complete within the allowed timeout of '00:00:03'."), out _));

        var snapshot = await timedOut.ProbeAsync(CancellationToken.None);
        var launch = await timedOut.LaunchAsync("PPSA99575", CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Contains("timeout", snapshot.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(launch.Dispatched);
        Assert.False(launch.Accepted);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => timedOut.ProbeAsync(cancelled.Token));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => timedOut.LaunchAsync("PPSA99575", cancelled.Token));
    }

    private static HttpResponseMessage Websrv(HttpRequestMessage request) => request.RequestUri!.AbsolutePath switch
    {
        "/version" => Json("""{"tag":"v0.30","api":1}"""),
        "/fs/user/app/" => Json("""[{"name":"PPSA99581","mode":"d"},{"name":"PPSA01325","mode":"d"}]"""),
        "/fs/user/appmeta/" => Json("[]"),
        "/launch" => new HttpResponseMessage(HttpStatusCode.OK),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task Websrv_probe_lists_installed_titles_without_running_state()
    {
        var snapshot = await new WebsrvClient(Client(Websrv, out _)).ProbeAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Contains("PPSA99581", snapshot.InstalledTitleIds);
        Assert.False(snapshot.RunningKnown);
    }

    [Fact]
    public async Task Websrv_launch_passes_the_title_id_and_maps_503_to_a_rejection()
    {
        var client = new WebsrvClient(Client(Websrv, out var handler));
        var ok = await client.LaunchAsync("PPSA99581", CancellationToken.None);
        var refused = await new WebsrvClient(Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable), out _)).LaunchAsync("PPSA99581", CancellationToken.None);

        Assert.True(ok.Accepted);
        Assert.Equal("?titleId=PPSA99581", handler.Requests.Single().RequestUri!.Query);
        Assert.True(refused.Dispatched);
        Assert.False(refused.Accepted);
    }
}

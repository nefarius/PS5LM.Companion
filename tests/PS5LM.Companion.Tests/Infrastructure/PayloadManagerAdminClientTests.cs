using System.Net;
using System.Text;

using Polly.Timeout;

using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Infrastructure.Setup;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class PayloadManagerAdminClientTests
{
    private static PayloadManagerAdminClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://ps5.test:8084/") });

    private static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };

    [Fact]
    public async Task Version_is_text_and_a_web_page_or_failure_is_not_a_version()
    {
        Assert.Equal("0.5.2", await Client(new StubHandler(_ => Text("0.5.2\n"))).GetVersionAsync(CancellationToken.None));
        Assert.Null(await Client(new StubHandler(_ => Text("<!doctype html>"))).GetVersionAsync(CancellationToken.None));
        Assert.Null(await Client(new StubHandler(_ => throw new HttpRequestException("down"))).GetVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_pipeline_timeout_is_no_version_and_a_finished_shutdown()
    {
        var timeout = new StubHandler(_ => throw new TimeoutRejectedException("stuck"));

        Assert.Null(await Client(timeout).GetVersionAsync(CancellationToken.None));
        await Client(timeout).ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_pipeline_timeout_after_the_caller_cancelled_is_not_swallowed()
    {
        using var versionCancel = new CancellationTokenSource();
        var version = new StubHandler(_ =>
        {
            versionCancel.Cancel();
            throw new TimeoutRejectedException("stuck");
        });
        using var shutdownCancel = new CancellationTokenSource();
        var shutdown = new StubHandler(_ =>
        {
            shutdownCancel.Cancel();
            throw new TimeoutRejectedException("stuck");
        });

        await Assert.ThrowsAsync<TimeoutRejectedException>(() => Client(version).GetVersionAsync(versionCancel.Token));
        await Assert.ThrowsAsync<TimeoutRejectedException>(() => Client(shutdown).ShutdownAsync(shutdownCancel.Token));
    }

    [Fact]
    public async Task List_reads_slots_on_06_and_paths_on_05()
    {
        var modern = await Client(new StubHandler(_ => Text("""{"items":[{"slot":"local/elfldr","path":"/p/elfldr.elf","filename":"elfldr.elf"},{"slot":"repo/ftpsrv@ab12","id":"ftpsrv","filename":"f.elf"}]}""")))
            .ListPayloadsAsync(CancellationToken.None);
        Assert.Equal(["local/elfldr", "repo/ftpsrv@ab12"], modern.Select(p => p.Slot).ToArray());
        Assert.Equal("ftpsrv", modern[1].Id);

        var legacy = await Client(new StubHandler(_ => Text("""{"payloads":["/data/pldmgr/payloads/elfldr/elfldr.elf"],"meta":{}}""")))
            .ListPayloadsAsync(CancellationToken.None);
        var item = Assert.Single(legacy);
        Assert.Equal("elfldr.elf", item.FileName);
        Assert.Equal("/data/pldmgr/payloads/elfldr/elfldr.elf", item.LaunchTarget);
    }

    [Fact]
    public async Task Upload_posts_the_body_as_octet_stream_with_the_manager_header()
    {
        var handler = new StubHandler(_ => Text("OK"));
        await Client(handler).UploadAsync("pldmgr_v0.6.3.elf", new MemoryStream([1, 2, 3]), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/manage:upload", request.RequestUri!.AbsolutePath);
        Assert.Equal("?filename=pldmgr_v0.6.3.elf", request.RequestUri.Query);
        Assert.Equal("1", request.Headers.GetValues("X-PLDMGR-Request").Single());
        Assert.Equal("application/octet-stream", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Upload_refusal_is_an_error()
    {
        var client = Client(new StubHandler(_ => Text("no", HttpStatusCode.Forbidden)));

        await Assert.ThrowsAsync<IOException>(() => client.UploadAsync("a.elf", new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task Load_keeps_slashes_and_reports_the_manager_answer()
    {
        var handler = new StubHandler(_ => Text("OK"));
        var result = await Client(handler).LoadAsync("local/my payload", CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal("/loadpayload:local/my%20payload", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.Equal("1", handler.Requests.Single().Headers.GetValues("X-PLDMGR-Request").Single());
        Assert.False((await Client(new StubHandler(_ => Text("Failed to launch payload\n", HttpStatusCode.InternalServerError))).LoadAsync("x", CancellationToken.None)).Accepted);
    }

    [Fact]
    public async Task Load_falls_back_to_get_when_the_manager_rejects_post()
    {
        var handler = new StubHandler(r => r.Method == HttpMethod.Post ? Text(string.Empty, HttpStatusCode.MethodNotAllowed) : Text("OK"));

        var result = await Client(handler).LoadAsync("/data/x.elf", CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal([HttpMethod.Post, HttpMethod.Get], handler.Requests.Select(r => r.Method).ToArray());
    }

    [Fact]
    public async Task Autoload_round_trips_through_the_config_calls()
    {
        var read = await Client(new StubHandler(_ => Text("""{"AUTOLOAD_ENABLED":true,"AUTOLOAD_LIST":"a,!3000,b"}""")))
            .GetAutoloadAsync(CancellationToken.None);
        Assert.Equal(new PS5LM.Companion.Core.Setup.AutoloadConfig(true, "a,!3000,b"), read);
        Assert.False(read!.DelayReported);

        var reported = await Client(new StubHandler(_ => Text("""{"AUTOLOAD_ENABLED":true,"AUTOLOAD_LIST":"a","AUTOLOAD_DELAY":"12"}"""))).GetAutoloadAsync(CancellationToken.None);
        Assert.Equal(12, reported!.DelaySeconds);
        Assert.True(reported.DelayReported);
        var numeric = await Client(new StubHandler(_ => Text("""{"AUTOLOAD_DELAY":15}"""))).GetAutoloadAsync(CancellationToken.None);
        Assert.Equal(15, numeric!.DelaySeconds);
        Assert.True(numeric.DelayReported);
        var zero = await Client(new StubHandler(_ => Text("""{"AUTOLOAD_DELAY":0}"""))).GetAutoloadAsync(CancellationToken.None);
        Assert.Equal(0, zero!.DelaySeconds);
        Assert.True(zero.DelayReported);
        var outOfRange = await Client(new StubHandler(_ => Text("""{"AUTOLOAD_DELAY":3601}"""))).GetAutoloadAsync(CancellationToken.None);
        Assert.Equal(0, outOfRange!.DelaySeconds);
        Assert.False(outOfRange.DelayReported);

        string? body = null;
        var handler = new StubHandler(r =>
        {
            body = r.Content!.ReadAsStringAsync().Result;
            return Text("OK");
        });
        await Client(handler).SetAutoloadAsync(true, "a,!3000", 15, CancellationToken.None);
        var request = handler.Requests.Single();
        Assert.Equal("/set_config", request.RequestUri!.AbsolutePath);
        Assert.Equal("1", request.Headers.GetValues("X-PLDMGR-Request").Single());
        Assert.Equal("""{"AUTOLOAD_ENABLED":true,"AUTOLOAD_LIST":"a,!3000","AUTOLOAD_DELAY":15}""", body);
    }

    [Fact]
    public async Task Processes_and_shutdown()
    {
        var processes = await Client(new StubHandler(_ => Text("""{"processes":[{"pid":1,"name":"kstuff.elf"}]}""")))
            .GetProcessesAsync(CancellationToken.None);
        Assert.Contains("KSTUFF.elf", processes!);

        var handler = new StubHandler(_ => Text("bye"));
        await Client(handler).ShutdownAsync(CancellationToken.None);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
        Assert.Equal("1", handler.Requests.Single().Headers.GetValues("X-PLDMGR-Request").Single());
    }

    [Fact]
    public async Task Autoload_status_reads_the_06_shape_the_05_countdown_and_a_failure()
    {
        var disabled = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "payload-manager", "0.6.3", "autoload_status.json"));
        var idle = await Client(new StubHandler(_ => Text(disabled))).GetAutoloadRunAsync(CancellationToken.None);
        Assert.Equal("disabled", idle!.State);
        Assert.Equal(-1, idle.RemainingSeconds);
        Assert.False(idle.InProgress);

        const string executing = """{"remaining":0,"remaining_ms":0,"total":7,"done":3,"current":"local/kstuff-lite","list":"local/elfldr,local/kstuff-lite","delay":10,"KILL_DISC_PLAYER_ON_STARTUP":true,"SCAN_USB_PAYLOADS":false,"state":"executing","successful":2,"failed":0,"skipped":0,"outcomes":"ssppppp","entry_index":2,"entry_duration_ms":8000,"entry_remaining_ms":1000}""";
        var busy = await Client(new StubHandler(_ => Text(executing))).GetAutoloadRunAsync(CancellationToken.None);
        Assert.Equal(new AutoloadRun("executing", 0, "local/kstuff-lite", 3, 7), busy);
        Assert.True(busy!.InProgress);

        var legacy = await Client(new StubHandler(_ => Text("""{"remaining":5}"""))).GetAutoloadRunAsync(CancellationToken.None);
        Assert.Equal(new AutoloadRun(null, 5, null, 0, 0), legacy);
        Assert.True(legacy!.InProgress);

        Assert.Null(await Client(new StubHandler(_ => Text("no", HttpStatusCode.InternalServerError))).GetAutoloadRunAsync(CancellationToken.None));
        Assert.True(idle.Understood);
        Assert.True(busy.Understood);
        Assert.True(legacy.Understood);

        var unknown = await Client(new StubHandler(_ => Text("""{"state":"paused","remaining":4}"""))).GetAutoloadRunAsync(CancellationToken.None);
        Assert.Equal("paused", unknown!.State);
        Assert.False(unknown.Understood);
        Assert.False(unknown.InProgress);

        var empty = await Client(new StubHandler(_ => Text("{}"))).GetAutoloadRunAsync(CancellationToken.None);
        Assert.False(empty!.Understood);
        Assert.False(empty.InProgress);
    }
}

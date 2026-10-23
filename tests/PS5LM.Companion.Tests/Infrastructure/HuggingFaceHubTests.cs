using System.Net;
using System.Net.Http.Headers;
using System.Text;

using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Infrastructure.Hub;
using PS5LM.Companion.Tests.Support;

using Refit;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class HuggingFaceHubTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));

    public HuggingFaceHubTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HuggingFaceHub Hub(StubHandler handler) =>
        new(RestService.For<IHuggingFaceApi>(new HttpClient(handler) { BaseAddress = new Uri("https://hf.test") }), new StubHttpClientFactory(handler));

    [Fact]
    public async Task Search_asks_for_gguf_repositories_ordered_by_downloads()
    {
        var handler = new StubHandler(_ => Json("""[{"id":"a/b-GGUF","downloads":5},{"downloads":1}]"""));

        var repos = await Hub(handler).SearchAsync("qwen", CancellationToken.None);

        Assert.Equal([new HubRepo("a/b-GGUF", 5)], repos);
        var query = handler.Requests.Single().RequestUri!.Query;
        Assert.Contains("search=qwen", query);
        Assert.Contains("filter=gguf", query);
        Assert.Contains("sort=downloads", query);
    }

    [Fact]
    public async Task Listing_keeps_top_level_gguf_files_with_their_lfs_size_and_hash()
    {
        var handler = new StubHandler(_ => Json("""
            [
              {"type":"file","path":"b-Q8.gguf","size":134,"lfs":{"oid":"abc123","size":900}},
              {"type":"file","path":"a-Q4.GGUF","size":50},
              {"type":"file","path":"README.md","size":10},
              {"type":"file","path":"sub/x.gguf","size":10},
              {"type":"directory","path":"dir.gguf","size":0}
            ]
            """));

        var files = await Hub(handler).ListFilesAsync("owner/name", CancellationToken.None);

        Assert.Equal([new HubFile("a-Q4.GGUF", 50, null), new HubFile("b-Q8.gguf", 900, "abc123")], files);
        Assert.Equal("/api/models/owner/name/tree/main", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_failing_hub_is_reported_as_unavailable()
    {
        var handler = new StubHandler(_ => Json("{}", HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<HubUnavailableException>(() => Hub(handler).SearchAsync("x", CancellationToken.None));
        await Assert.ThrowsAsync<HubUnavailableException>(() => Hub(handler).ListFilesAsync("a/b", CancellationToken.None));
    }

    [Fact]
    public async Task A_resilience_timeout_or_open_circuit_is_reported_as_unavailable()
    {
        var timeout = new StubHandler(_ => throw new Polly.Timeout.TimeoutRejectedException("timed out"));
        var broken = new StubHandler(_ => throw new Polly.CircuitBreaker.BrokenCircuitException("open"));

        await Assert.ThrowsAsync<HubUnavailableException>(() => Hub(timeout).SearchAsync("x", CancellationToken.None));
        await Assert.ThrowsAsync<HubUnavailableException>(() => Hub(broken).ListFilesAsync("a/b", CancellationToken.None));
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_stalled_download_fails_with_an_io_error_instead_of_blocking_forever()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        var hub = new HuggingFaceHub(RestService.For<IHuggingFaceApi>(new HttpClient(handler) { BaseAddress = new Uri("https://hf.test") }), new StubHttpClientFactory(handler))
        {
            IdleTimeout = TimeSpan.FromMilliseconds(100),
        };

        await Assert.ThrowsAsync<IOException>(() => hub.DownloadAsync("a/b", "m.gguf", Path.Combine(_dir, "m.gguf"), null, CancellationToken.None));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task A_hub_that_never_sends_headers_fails_with_an_io_error()
    {
        var handler = new HangingHandler();
        var hub = new HuggingFaceHub(RestService.For<IHuggingFaceApi>(new HttpClient(handler) { BaseAddress = new Uri("https://hf.test") }), new StubHttpClientFactory(handler))
        {
            HeaderTimeout = TimeSpan.FromMilliseconds(100),
        };

        await Assert.ThrowsAsync<IOException>(() => hub.DownloadAsync("a/b", "m.gguf", Path.Combine(_dir, "m.gguf"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Caller_cancellation_of_a_stalled_download_is_still_cancellation()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Hub(handler).DownloadAsync("a/b", "m.gguf", Path.Combine(_dir, "m.gguf"), null, cts.Token));
    }

    [Fact]
    public async Task A_fresh_download_writes_the_whole_body()
    {
        var path = Path.Combine(_dir, "m.gguf");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeHub.Content) });

        await Hub(handler).DownloadAsync("a/b", "m.gguf", path, null, CancellationToken.None);

        Assert.Equal(FakeHub.Content, await File.ReadAllBytesAsync(path));
        Assert.Null(handler.Requests.Single().Headers.Range);
        Assert.Equal("/a/b/resolve/main/m.gguf", handler.Requests.Single().RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task A_partial_file_is_resumed_with_a_range_request()
    {
        var path = Path.Combine(_dir, "m.gguf");
        await File.WriteAllBytesAsync(path, FakeHub.Content[..2000]);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(FakeHub.Content[2000..]) });

        await Hub(handler).DownloadAsync("a/b", "m.gguf", path, null, CancellationToken.None);

        Assert.Equal(new RangeHeaderValue(2000, null).ToString(), handler.Requests.Single().Headers.Range!.ToString());
        Assert.Equal(FakeHub.Content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_restarts_instead_of_corrupting_the_file()
    {
        var path = Path.Combine(_dir, "m.gguf");
        await File.WriteAllBytesAsync(path, FakeHub.Content[..2000]);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeHub.Content) });

        await Hub(handler).DownloadAsync("a/b", "m.gguf", path, null, CancellationToken.None);

        Assert.Equal(FakeHub.Content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_complete_partial_file_answered_with_416_is_left_for_verification()
    {
        var path = Path.Combine(_dir, "m.gguf");
        await File.WriteAllBytesAsync(path, FakeHub.Content);
        long reported = 0;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

        await Hub(handler).DownloadAsync("a/b", "m.gguf", path, new Progress<long>(b => reported = b), CancellationToken.None);

        Assert.Equal(FakeHub.Content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_refused_download_throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Hub(handler).DownloadAsync("a/b", "m.gguf", Path.Combine(_dir, "m.gguf"), null, CancellationToken.None));
    }
}

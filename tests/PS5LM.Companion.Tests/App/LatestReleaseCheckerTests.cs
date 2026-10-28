using System.Net;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.App.Updates;

namespace PS5LM.Companion.Tests.App;

public sealed class LatestReleaseCheckerTests
{
    private static readonly Uri ReleasePage = new("https://github.com/nefarius/PS5LM.Companion/releases/tag/v1.2.0");

    [Fact]
    public async Task A_newer_stable_release_links_to_its_page()
    {
        var handler = new RecordingHandler(ReleaseJson("v1.2.0", ReleasePage));
        using var checker = Create(handler, "1.0.1");

        var status = await checker.GetStatusAsync();

        Assert.Equal("1.0.1", status.LocalVersion);
        Assert.Equal("v1.2.0", status.LatestVersion);
        Assert.True(status.UpdateAvailable);
        Assert.Equal(ReleasePage, status.ReleaseUri);
        Assert.Equal("v1.0.1 · v1.2.0 available", status.ChipLabel);
        Assert.Equal(LatestReleaseChecker.LatestReleaseUri, handler.LastRequest?.RequestUri);
        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
    }

    [Fact]
    public async Task The_request_identifies_the_companion_to_the_GitHub_api()
    {
        var handler = new RecordingHandler(ReleaseJson("v1.2.0", ReleasePage));
        using var client = ConfiguredClient(handler);
        using var checker = new LatestReleaseChecker(client, new FixedVersion("1.0.0"), TimeProvider.System);

        await checker.GetStatusAsync();

        var request = handler.LastRequest!;
        Assert.Equal("PS5LM.Companion", request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0")]
    [InlineData("v0.9.0")]
    public async Task The_same_or_older_release_leaves_the_chip_unchanged(string tag)
    {
        var handler = new RecordingHandler(ReleaseJson(tag, ReleasePage));
        using var checker = Create(handler, "1.0.0");

        var status = await checker.GetStatusAsync();

        Assert.False(status.UpdateAvailable);
        Assert.Null(status.LatestVersion);
        Assert.Null(status.ReleaseUri);
        Assert.Equal("v1.0.0", status.ChipLabel);
        Assert.Equal("Running version v1.0.0.", status.Tooltip);
    }

    [Fact]
    public async Task A_development_build_ahead_of_the_stable_release_is_not_an_update()
    {
        var handler = new RecordingHandler(ReleaseJson("v1.0.1", ReleasePage));
        using var checker = Create(handler, "1.0.2-alpha.0.2");

        var status = await checker.GetStatusAsync();

        Assert.False(status.UpdateAvailable);
        Assert.Equal("v1.0.2-alpha.0.2", status.ChipLabel);
    }

    [Fact]
    public async Task A_prerelease_build_offers_the_matching_stable_release()
    {
        var page = new Uri("https://github.com/nefarius/PS5LM.Companion/releases/tag/v1.0.2");
        var handler = new RecordingHandler(ReleaseJson("v1.0.2", page));
        using var checker = Create(handler, "1.0.2-alpha.0.2");

        var status = await checker.GetStatusAsync();

        Assert.True(status.UpdateAvailable);
        Assert.Equal(page, status.ReleaseUri);
        Assert.Equal("v1.0.2-alpha.0.2 · v1.0.2 available", status.ChipLabel);
    }

    [Theory]
    [InlineData("""{"tag_name":"v9.0.0","prerelease":true,"html_url":"https://github.com/nefarius/PS5LM.Companion/releases/tag/v9.0.0"}""")]
    [InlineData("""{"tag_name":"v9.0.0","draft":true,"html_url":"https://github.com/nefarius/PS5LM.Companion/releases/tag/v9.0.0"}""")]
    [InlineData("""{"html_url":"https://github.com/nefarius/PS5LM.Companion/releases/tag/v9.0.0"}""")]
    [InlineData("""{"tag_name":"not-a-version","html_url":"https://github.com/nefarius/PS5LM.Companion/releases/tag/not-a-version"}""")]
    public async Task Unusable_release_payloads_do_not_report_an_update(string body)
    {
        var handler = new RecordingHandler(body);
        using var checker = Create(handler, "1.0.0");

        var status = await checker.GetStatusAsync();

        Assert.Equal("1.0.0", status.LocalVersion);
        Assert.False(status.UpdateAvailable);
        Assert.Null(status.ReleaseUri);
    }

    [Theory]
    [InlineData("http://github.com/nefarius/PS5LM.Companion/releases/tag/v2.0.0")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/releases/tag/v2.0.0")]
    [InlineData("")]
    public async Task A_newer_release_without_an_https_page_still_warns(string page)
    {
        var handler = new RecordingHandler(ReleaseJson("v2.0.0", page));
        using var checker = Create(handler, "1.0.0");

        var status = await checker.GetStatusAsync();

        Assert.True(status.UpdateAvailable);
        Assert.Null(status.ReleaseUri);
        Assert.Equal("v1.0.0 · v2.0.0 available", status.ChipLabel);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"message":"Not Found"}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"message":"rate limit"}""")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task Failed_lookups_keep_the_local_version_and_are_cached(HttpStatusCode statusCode, string body)
    {
        var time = Clock();
        var handler = new RecordingHandler(body, statusCode);
        using var checker = Create(handler, "1.4.0", time, TimeSpan.FromMinutes(15));

        var status = await checker.GetStatusAsync();
        var cached = await checker.GetStatusAsync();

        Assert.Equal("1.4.0", status.LocalVersion);
        Assert.False(status.UpdateAvailable);
        Assert.Equal("Running version v1.4.0.", status.Tooltip);
        Assert.False(cached.UpdateAvailable);
        Assert.Equal(1, handler.Calls);

        time.Advance(TimeSpan.FromMinutes(15));
        handler.Body = ReleaseJson("v9.0.0", ReleasePage);
        handler.StatusCode = HttpStatusCode.OK;
        var refreshed = await checker.GetStatusAsync();

        Assert.Equal(2, handler.Calls);
        Assert.True(refreshed.UpdateAvailable);
    }

    [Fact]
    public async Task A_transport_failure_keeps_the_local_version()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("offline"));
        using var checker = Create(handler, " 1.4.0 ");

        var status = await checker.GetStatusAsync();

        Assert.Equal("1.4.0", status.LocalVersion);
        Assert.False(status.UpdateAvailable);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void The_local_chip_does_not_contact_GitHub()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("offline"));
        using var checker = Create(handler, "1.5.0");

        var status = checker.GetLocalStatus();

        Assert.Equal("v1.5.0", status.ChipLabel);
        Assert.Equal(0, handler.Calls);
        Assert.False(status.UpdateAvailable);
    }

    [Fact]
    public async Task An_assembly_lookup_failure_stays_unknown_and_does_not_report_an_update()
    {
        var handler = new RecordingHandler(ReleaseJson("v2.0.0", ReleasePage));
        using var checker = Create(handler, new ThrowingVersion());

        var local = checker.GetLocalStatus();
        var status = await checker.GetStatusAsync();

        Assert.Equal("unknown", local.ChipLabel);
        Assert.Equal("Version unavailable", local.Tooltip);
        Assert.False(status.UpdateAvailable);
        Assert.Equal("unknown", status.ChipLabel);
    }

    [Fact]
    public async Task A_successful_response_is_reused_until_the_cache_expires()
    {
        var time = new ManualTime();
        var handler = new RecordingHandler(ReleaseJson("v2.0.0", ReleasePage));
        using var checker = Create(handler, "1.0.0", time, TimeSpan.FromMinutes(15));

        var first = await checker.GetStatusAsync();
        var second = await checker.GetStatusAsync();
        Assert.Equal(1, handler.Calls);
        Assert.Equal(first, second);

        time.UtcNow -= TimeSpan.FromSeconds(1);
        handler.Body = ReleaseJson("v3.0.0", ReleasePage);
        var backwards = await checker.GetStatusAsync();
        Assert.Equal("v3.0.0", backwards.LatestVersion);

        time.UtcNow += TimeSpan.FromMinutes(14);
        await checker.GetStatusAsync();
        Assert.Equal(2, handler.Calls);

        time.UtcNow += TimeSpan.FromMinutes(15);
        handler.Body = ReleaseJson("v4.0.0", ReleasePage);
        var expired = await checker.GetStatusAsync();

        Assert.Equal(3, handler.Calls);
        Assert.Equal("v4.0.0", expired.LatestVersion);
    }

    [Fact]
    public async Task Concurrent_calls_share_one_request()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Response(ReleaseJson("v9.0.0", ReleasePage));
        });
        using var checker = Create(handler, "1.0.0");

        var first = checker.GetStatusAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = checker.GetStatusAsync();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(second.IsCompleted);
            await Task.Delay(10);
        }

        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(results[0], results[1]);
        Assert.Equal("v9.0.0", results[0].LatestVersion);
    }

    [Fact]
    public async Task Cancellation_is_not_cached()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHandler(async (_, token) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }

            return Response(ReleaseJson("v2.0.0", ReleasePage));
        });
        using var checker = Create(handler, "1.0.0");
        using var cancellation = new CancellationTokenSource();

        var pending = checker.GetStatusAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var status = await checker.GetStatusAsync();

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.True(status.UpdateAvailable);
        Assert.Equal(ReleasePage, status.ReleaseUri);
    }

    [Fact]
    public void The_checker_is_registered_once_for_the_process()
    {
        var services = new ServiceCollection();
        services.AddUpdateCheck();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<LatestReleaseChecker>();
        var second = provider.GetRequiredService<LatestReleaseChecker>();
        var status = first.GetLocalStatus();

        Assert.Same(first, second);
        Assert.True(status.HasLocalVersion);
        Assert.StartsWith("v", status.ChipLabel);
    }

    [Fact]
    public void Construction_rejects_a_non_positive_cache_lifetime()
    {
        var handler = new RecordingHandler("{}");
        using var client = new HttpClient(handler, disposeHandler: true);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new LatestReleaseChecker(client, new FixedVersion("1.0.0"), cacheLifetime: TimeSpan.Zero));

        Assert.Equal("cacheLifetime", exception.ParamName);
    }

    private static LatestReleaseChecker Create(
        HttpMessageHandler handler,
        string? version,
        TimeProvider? time = null,
        TimeSpan? lifetime = null) =>
        Create(handler, new FixedVersion(version), time, lifetime);

    private static LatestReleaseChecker Create(
        HttpMessageHandler handler,
        IAssemblyVersionSource versions,
        TimeProvider? time = null,
        TimeSpan? lifetime = null)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        return new LatestReleaseChecker(client, versions, time ?? TimeProvider.System, lifetime);
    }

    private static HttpClient ConfiguredClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        LatestReleaseChecker.ConfigureClient(client);
        return client;
    }

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));

    private static string ReleaseJson(string tag, Uri page) => ReleaseJson(tag, page.ToString());

    private static string ReleaseJson(string tag, string page) => $$"""
        {
          "url": "https://api.github.com/repos/nefarius/PS5LM.Companion/releases/1",
          "tag_name": "{{tag}}",
          "name": "{{tag}}",
          "draft": false,
          "prerelease": false,
          "html_url": "{{page}}"
        }
        """;

    private static HttpResponseMessage Response(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FixedVersion(string? version) : IAssemblyVersionSource
    {
        public string? TryGetVersion() => version;
    }

    private sealed class ThrowingVersion : IAssemblyVersionSource
    {
        public string? TryGetVersion() => throw new InvalidOperationException("version unavailable");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage>? _respond;

        public RecordingHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            Body = body;
            StatusCode = statusCode;
        }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public string Body { get; set; } = "";

        public HttpStatusCode StatusCode { get; set; }

        public int Calls { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (_respond is not null)
            {
                return Task.FromResult(_respond(request));
            }

            return Task.FromResult(Response(Body, StatusCode));
        }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}

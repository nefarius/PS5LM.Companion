using System.Net;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Infrastructure;
using PS5LM.Companion.Infrastructure.Http;

using Refit;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class PayloadManagerClientTests
{
    [Fact]
    public async Task List_history_and_autoload_agree_on_slots_and_ignore_extra_fields()
    {
        const string list = """
            {"self":{"id":"pldmgr","running_version":"0.6.3"},"items":[
              {"slot":"repo/kstuff-lite@abcdef12","id":"kstuff-lite","name":"kstuff-lite","filename":"kstuff-lite_v1.11.elf","extra":true},
              {"slot":"repo/elfldr@1234abcd","id":"elfldr","name":"elfldr"},
              {"slot":"","id":"ignored"}
            ],"usb":[],"payloads":[]}
            """;
        var handler = new RoutingHandler(path => path switch
        {
            "/list_payloads" => Json(list),
            "/history_list" => Json("""{"history":["/data/pldmgr/payloads/repo/kstuff-lite@abcdef12/kstuff-lite_v1.11.elf"]}"""),
            "/autoload_status" => Json("""{"list":"repo/elfldr@1234abcd, repo/kstuff-lite@abcdef12","outcomes":"sf","state":"done"}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var snapshot = await Client(handler).ReadAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Equal(["kstuff-lite", "elfldr"], snapshot.Installed.Select(payload => payload.Id).ToArray());
        Assert.Equal(["repo/elfldr@1234abcd", "repo/kstuff-lite@abcdef12"], snapshot.LaunchRecordedSlots.OrderBy(slot => slot, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_refused_list_is_an_unavailable_manager()
    {
        var handler = new RoutingHandler(_ => throw new HttpRequestException("refused"));

        var snapshot = await Client(handler).ReadAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Contains("did not answer", snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_that_never_answers_fails_the_read_instead_of_waiting_forever()
    {
        var handler = new HangingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://pld.test"), Timeout = TimeSpan.FromMilliseconds(100) };

        var snapshot = await Client(http).ReadAsync(CancellationToken.None);

        Assert.False(snapshot.Available);
        Assert.Contains("did not answer", snapshot.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_still_propagates()
    {
        var http = new HttpClient(new HangingHandler()) { BaseAddress = new Uri("http://pld.test"), Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(http).ReadAsync(cts.Token));
    }

    [Fact]
    public async Task Launch_encodes_the_slot_and_sends_the_manager_header()
    {
        HttpRequestMessage? seen = null;
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("OK", Encoding.UTF8, "text/plain"),
        })
        {
            Capture = request => seen = request,
        };

        var result = await Client(handler).LaunchAsync("repo/elfldr@1234abcd", CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Contains("loadpayload:repo%2Felfldr%401234abcd", seen.RequestUri!.OriginalString, StringComparison.Ordinal);
        Assert.Equal("1", seen.Headers.GetValues("X-PLDMGR-Request").Single());
    }

    [Fact]
    public async Task A_failed_launch_keeps_the_manager_status_and_body()
    {
        var handler = new RoutingHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Failed to launch payload"),
        });

        var result = await Client(handler).LaunchAsync("repo/etahen@abc", CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(500, result.StatusCode);
        Assert.Contains("Failed to launch payload", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_manager_url_must_be_absolute_http()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PS5:Host"] = "ps5",
                ["PS5:PayloadManagerUrl"] = "ftp://ps5/mgr",
            })
            .Build();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(configuration)
            .AddPS5Connectivity(configuration)
            .BuildServiceProvider(validateScopes: true);

        var exception = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<PS5Options>());
        Assert.Contains("PayloadManagerUrl", exception.Message, StringComparison.Ordinal);
    }

    private static PayloadManagerClient Client(HttpMessageHandler handler) =>
        Client(new HttpClient(handler) { BaseAddress = new Uri("http://pld.test") });

    private static PayloadManagerClient Client(HttpClient http) =>
        new(RestService.For<IPayloadManagerApi>(http));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RoutingHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Action<HttpRequestMessage>? Capture { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Capture?.Invoke(request);
            var response = respond(request.RequestUri!.AbsolutePath);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}

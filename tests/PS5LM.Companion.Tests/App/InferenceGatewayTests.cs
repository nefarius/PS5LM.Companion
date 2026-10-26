using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class InferenceGatewayTests
{
    private const string LoadedPath = "/data/PS5LM/models/qwen.gguf";

    /// <summary>The app wired to fake consoles whose llama-servers are real local listeners.</summary>
    private sealed class Cluster : IAsyncDisposable
    {
        public required CompanionFactory Factory { get; init; }

        public required Dictionary<string, FakeUpstream> Upstreams { get; init; }

        public static async Task<Cluster> StartAsync(
            string[] ids,
            IReadOnlyDictionary<string, string>? settings = null,
            string? token = null,
            Func<string, string>? loaded = null,
            bool pools = true)
        {
            var upstreams = new Dictionary<string, FakeUpstream>();
            foreach (var id in ids)
            {
                upstreams[id] = await FakeUpstream.StartAsync(id);
            }

            var configuration = new Dictionary<string, string> { ["Inference:QueueTimeoutSeconds"] = "5" };
            if (pools)
            {
                configuration["Inference:Pools:chat:Model"] = "qwen.gguf";
                for (var i = 0; i < ids.Length; i++)
                {
                    configuration[$"Inference:Pools:chat:Consoles:{i}"] = ids[i];
                }
            }

            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                configuration[key] = value;
            }

            var factory = new CompanionFactory
            {
                ConsoleIds = ids,
                AccessToken = token,
                LlamaPorts = upstreams.ToDictionary(u => u.Key, u => u.Value.Port),
                Settings = configuration,
            };
            foreach (var id in ids)
            {
                factory.Connectors[id].Llama.Result = new LlamaProbeResult(LlamaServerState.Serving, FakeLlamaProbe.Metrics(10, 1), (loaded ?? (_ => LoadedPath))(id));
            }

            var cluster = new Cluster { Factory = factory, Upstreams = upstreams };
            await cluster.PublishAsync();
            return cluster;
        }

        public async Task PublishAsync()
        {
            foreach (var console in Factory.Registry.All)
            {
                console.Store.Publish(await console.Status.RefreshAsync(CancellationToken.None));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            foreach (var upstream in Upstreams.Values)
            {
                await upstream.DisposeAsync();
            }
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static int Total(Cluster cluster) => cluster.Upstreams.Values.Sum(u => u.Requests.Count);

    [Fact]
    public async Task A_chat_request_is_forwarded_byte_for_byte_and_answered_by_the_console()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        using var client = cluster.Factory.CreateClient();
        const string body = """{"model":"chat","messages":[{"role":"user","content":"hi"}],"temperature":0.0}""";

        using var response = await client.PostAsync("/v1/chat/completions?x=1", Json(body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("a", response.Headers.GetValues("X-PS5LM-Console").Single());
        Assert.Equal("a", response.Headers.GetValues("X-Upstream").Single());
        var received = Assert.Single(cluster.Upstreams["a"].Requests);
        Assert.Equal("POST", received.Method);
        Assert.Equal("/v1/chat/completions", received.Path);
        Assert.Equal("?x=1", received.Query);
        Assert.Equal(body, received.Body);
        Assert.Equal("a", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("upstream").GetString());
    }

    [Fact]
    public async Task An_alternate_name_reaches_the_pool_and_stays_out_of_the_model_list()
    {
        await using var cluster = await Cluster.StartAsync(["a"], new Dictionary<string, string> { ["Inference:Pools:chat:AlternateNames:0"] = "claude-*" });
        using var client = cluster.Factory.CreateClient();

        using var response = await client.PostAsync("/v1/chat/completions", Json("""{"model":"claude-sonnet-4-5","messages":[]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("chat", response.Headers.GetValues("X-PS5LM-Pool").Single());
        var models = await client.GetFromJsonAsync<JsonElement>("/v1/models");
        Assert.Equal("chat", Assert.Single(models.GetProperty("data").EnumerateArray()).GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("/v1/completions")]
    [InlineData("/v1/responses")]
    [InlineData("/v1/messages")]
    [InlineData("/v1/systemone")]
    [InlineData("/v1/chat/completions/input_tokens")]
    [InlineData("/v1/responses/input_tokens")]
    [InlineData("/v1/messages/count_tokens")]
    public async Task Every_inference_route_is_forwarded(string path)
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        using var client = cluster.Factory.CreateClient();

        using var response = await client.PostAsync(path, Json("""{"model":"chat"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(path, cluster.Upstreams["a"].Requests.Single().Path);
    }

    [Theory]
    [InlineData("/props")]
    [InlineData("/slots")]
    [InlineData("/v1/embeddings")]
    [InlineData("/v1/chat/completions/control")]
    [InlineData("/models/load")]
    public async Task Admin_and_unsupported_routes_are_not_forwarded(string path)
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        using var client = cluster.Factory.CreateClient();

        using var post = await client.PostAsync(path, Json("""{"model":"chat"}"""));
        using var get = await client.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(0, Total(cluster));
    }

    [Fact]
    public async Task Requests_never_reach_a_console_that_has_another_model_loaded()
    {
        await using var cluster = await Cluster.StartAsync(["a", "b", "c"], loaded: id => id == "b" ? "/data/PS5LM/models/llama.gguf" : LoadedPath);
        using var client = cluster.Factory.CreateClient();

        for (var i = 0; i < 6; i++)
        {
            using var response = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Empty(cluster.Upstreams["b"].Requests);
        Assert.Equal(3, cluster.Upstreams["a"].Requests.Count);
        Assert.Equal(3, cluster.Upstreams["c"].Requests.Count);
    }

    [Fact]
    public async Task Concurrent_requests_spread_over_the_consoles()
    {
        await using var cluster = await Cluster.StartAsync(["a", "b"]);
        var release = new TaskCompletionSource();
        foreach (var upstream in cluster.Upstreams.Values)
        {
            upstream.Handler = async (context, _) =>
            {
                await release.Task;
                await context.Response.WriteAsJsonAsync(new { ok = true });
            };
        }

        using var client = cluster.Factory.CreateClient();
        var first = client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        var second = client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        await WaitUntilAsync(() => Total(cluster) == 2);
        release.SetResult();

        using var one = await first;
        using var two = await second;
        Assert.Single(cluster.Upstreams["a"].Requests);
        Assert.Single(cluster.Upstreams["b"].Requests);
        Assert.NotEqual(one.Headers.GetValues("X-PS5LM-Console").Single(), two.Headers.GetValues("X-PS5LM-Console").Single());
    }

    [Fact]
    public async Task A_busy_console_makes_the_next_request_wait_for_its_slot()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        var release = new TaskCompletionSource();
        cluster.Upstreams["a"].Handler = async (context, _) =>
        {
            await release.Task;
            await context.Response.WriteAsJsonAsync(new { ok = true });
        };
        using var client = cluster.Factory.CreateClient();

        var first = client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        await WaitUntilAsync(() => Total(cluster) == 1);
        var second = client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        await Task.Delay(200);
        Assert.Equal(1, Total(cluster));
        release.SetResult();

        using var one = await first;
        using var two = await second;
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        Assert.Equal(2, Total(cluster));
    }

    [Fact]
    public async Task Server_sent_events_are_streamed_not_buffered()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        var second = new TaskCompletionSource();
        cluster.Upstreams["a"].Handler = async (context, _) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: one\n\n");
            await context.Response.Body.FlushAsync();
            await second.Task;
            await context.Response.WriteAsync("data: two\n\n");
        };
        using var client = cluster.Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Json("""{"model":"chat","stream":true}""") };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        var firstEvent = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        second.SetResult();
        await reader.ReadLineAsync(); // blank line
        var secondEvent = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("data: one", firstEvent);
        Assert.Equal("data: two", secondEvent);
    }

    [Fact]
    public async Task Upstream_status_and_body_pass_through()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        cluster.Upstreams["a"].Handler = async (context, _) =>
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("""{"error":"upstream says no"}""");
        };
        using var client = cluster.Factory.CreateClient();

        using var response = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"upstream says no"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_console_that_is_down_gives_a_json_bad_gateway_and_frees_its_slot()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        await cluster.Upstreams["a"].DisposeAsync(); // the port is now closed, but the status still says Ready
        using var client = cluster.Factory.CreateClient();

        using var first = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        using var second = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));

        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Equal("api_error", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode); // not stuck in the queue
    }

    [Fact]
    public async Task A_client_that_disconnects_cancels_the_upstream_request_and_frees_the_slot()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        var upstreamCancelled = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        cluster.Upstreams["a"].Handler = async (context, _) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                upstreamCancelled.TrySetResult();
            }
        };
        using var client = cluster.Factory.CreateClient();
        using var cts = new CancellationTokenSource();

        var pending = client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cluster.Upstreams["a"].Handler = null;
        await WaitUntilAsync(async () =>
        {
            var pools = await client.GetFromJsonAsync<JsonElement>("/api/inference/pools");
            return pools.GetProperty("pools")[0].GetProperty("replicas")[0].GetProperty("activeRequests").GetInt32() == 0;
        });
        using var next = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task Unknown_models_and_missing_replicas_are_api_style_errors()
    {
        await using var cluster = await Cluster.StartAsync(["a"], loaded: _ => "/data/PS5LM/models/other.gguf");
        using var client = cluster.Factory.CreateClient();

        using var unknown = await client.PostAsync("/v1/chat/completions", Json("""{"model":"ghost"}"""));
        using var none = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        using var anthropic = await client.PostAsync("/v1/messages", Json("""{"model":"ghost"}"""));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("model_not_found", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, none.StatusCode);
        Assert.True(none.Headers.Contains("Retry-After"));
        var shape = await anthropic.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("error", shape.GetProperty("type").GetString());
        Assert.Equal(0, Total(cluster));
    }

    [Fact]
    public async Task A_request_without_a_model_uses_the_only_pool_but_is_rejected_when_there_are_several()
    {
        await using var single = await Cluster.StartAsync(["a"]);
        using var singleClient = single.Factory.CreateClient();
        await using var several = await Cluster.StartAsync(["a"], new Dictionary<string, string>
        {
            ["Inference:Pools:other:Model"] = "other.gguf",
            ["Inference:Pools:other:Consoles:0"] = "a",
        });
        using var severalClient = several.Factory.CreateClient();

        using var accepted = await singleClient.PostAsync("/v1/systemone", Json("""{"state":"x","questions":{}}"""));
        using var rejected = await severalClient.PostAsync("/v1/systemone", Json("""{"state":"x","questions":{}}"""));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(0, Total(several));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"model":5}""")]
    [InlineData("""{"model":" "}""")]
    public async Task Malformed_bodies_are_rejected_before_any_console_is_chosen(string body)
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        using var client = cluster.Factory.CreateClient();

        using var response = await client.PostAsync("/v1/chat/completions", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, Total(cluster));
    }

    [Fact]
    public async Task An_oversized_body_is_rejected()
    {
        await using var cluster = await Cluster.StartAsync(["a"], new Dictionary<string, string> { ["Inference:MaxRequestBodyMegabytes"] = "1" });
        using var client = cluster.Factory.CreateClient();
        var body = $$"""{"model":"chat","pad":"{{new string('x', 2 * 1024 * 1024)}}"}""";

        using var response = await client.PostAsync("/v1/chat/completions", Json(body));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, Total(cluster));
    }

    [Fact]
    public async Task The_access_token_is_required_in_either_style_and_never_reaches_the_console()
    {
        await using var cluster = await Cluster.StartAsync(["a"], token: "s3cret");
        using var client = cluster.Factory.CreateClient();

        using var anonymous = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = Json("""{"model":"chat"}""") };
        wrong.Headers.Add("x-api-key", "nope");
        using var wrongResponse = await client.SendAsync(wrong);
        using var bearer = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Json("""{"model":"chat"}""") };
        bearer.Headers.Authorization = new("Bearer", "s3cret");
        using var bearerResponse = await client.SendAsync(bearer);
        using var anthropic = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = Json("""{"model":"chat"}""") };
        anthropic.Headers.Add("x-api-key", "s3cret");
        anthropic.Headers.Add("anthropic-version", "2023-06-01");
        using var anthropicResponse = await client.SendAsync(anthropic);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("invalid_api_key", (await anonymous.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, bearerResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, anthropicResponse.StatusCode);
        Assert.Equal(2, cluster.Upstreams["a"].Requests.Count);
        foreach (var request in cluster.Upstreams["a"].Requests)
        {
            Assert.DoesNotContain("Authorization", request.Headers.Keys);
            Assert.DoesNotContain("x-api-key", request.Headers.Keys);
            Assert.DoesNotContain("s3cret", string.Join(' ', request.Headers.Values), StringComparison.Ordinal);
        }

        Assert.Contains("anthropic-version", cluster.Upstreams["a"].Requests.Last().Headers.Keys);
    }

    [Fact]
    public async Task Models_lists_the_aliases_with_their_ready_replicas()
    {
        await using var cluster = await Cluster.StartAsync(["a", "b"], loaded: id => id == "b" ? "/data/PS5LM/models/llama.gguf" : LoadedPath);
        using var client = cluster.Factory.CreateClient();

        var models = await client.GetFromJsonAsync<JsonElement>("/v1/models");
        var pools = await client.GetFromJsonAsync<JsonElement>("/api/inference/pools");

        var model = models.GetProperty("data").EnumerateArray().Single();
        Assert.Equal("list", models.GetProperty("object").GetString());
        Assert.Equal("chat", model.GetProperty("id").GetString());
        Assert.Equal(2, model.GetProperty("ps5lm").GetProperty("replicas").GetInt32());
        Assert.Equal(1, model.GetProperty("ps5lm").GetProperty("ready").GetInt32());
        Assert.True(pools.GetProperty("enabled").GetBoolean());
        var replicas = pools.GetProperty("pools")[0].GetProperty("replicas").EnumerateArray().ToDictionary(r => r.GetProperty("consoleId").GetString()!);
        Assert.True(replicas["a"].GetProperty("eligible").GetBoolean());
        Assert.False(replicas["b"].GetProperty("eligible").GetBoolean());
        Assert.Contains("llama.gguf", replicas["b"].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, Total(cluster));
    }

    [Fact]
    public async Task A_pool_added_after_startup_is_served_without_a_restart()
    {
        await using var cluster = await Cluster.StartAsync(["a"], pools: false);
        using var client = cluster.Factory.CreateClient();

        using var before = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));
        var pools = await client.GetFromJsonAsync<JsonElement>("/api/inference/pools");

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal("model_not_found", (await before.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(0, Total(cluster));
        Assert.False(pools.GetProperty("enabled").GetBoolean());
        Assert.Empty(pools.GetProperty("pools").EnumerateArray());

        using var created = await client.PostAsJsonAsync("/api/inference/pools", new { alias = "chat", model = "qwen.gguf", consoles = new[] { "a" } });
        using var after = await client.PostAsync("/v1/chat/completions", Json("""{"model":"chat"}"""));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal(1, Total(cluster));
    }

    [Fact]
    public async Task The_process_health_check_is_unchanged()
    {
        await using var cluster = await Cluster.StartAsync(["a"], loaded: _ => "/data/PS5LM/models/other.gguf");
        using var client = cluster.Factory.CreateClient();

        using var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static Task Completion(HttpContext context, string _) =>
        context.Response.WriteAsJsonAsync(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "ok" } } },
            usage = new { prompt_tokens = 12, completion_tokens = 1 },
        });

    private static async Task Stream(HttpContext context, string _)
    {
        context.Response.ContentType = "text/event-stream";
        foreach (var piece in new[] { "The", " sky", " is", " blue" })
        {
            await context.Response.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{piece}\"}}}}]}}\n\n");
            await context.Response.Body.FlushAsync();
            await Task.Delay(20);
        }

        await context.Response.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":4},\"timings\":{\"prompt_per_second\":55.5,\"predicted_per_second\":7.25}}\n\n");
        await context.Response.WriteAsync("data: [DONE]\n\n");
    }

    [Fact]
    public async Task The_self_test_checks_every_ready_console_and_skips_the_others()
    {
        await using var cluster = await Cluster.StartAsync(["a", "b", "c"], loaded: id => id == "c" ? "/data/PS5LM/models/llama.gguf" : LoadedPath);
        cluster.Upstreams["a"].Handler = Completion;
        cluster.Upstreams["b"].Handler = async (context, _) =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("boom");
        };
        using var client = cluster.Factory.CreateClient();

        using var response = await client.PostAsync("/api/inference/pools/chat/test", null);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(run.GetProperty("passed").GetBoolean()); // b failed
        var consoles = run.GetProperty("consoles").EnumerateArray().ToDictionary(c => c.GetProperty("consoleId").GetString()!);
        Assert.Equal("Passed", consoles["a"].GetProperty("outcome").GetString());
        Assert.Equal("ok", consoles["a"].GetProperty("reply").GetString());
        Assert.Equal(12, consoles["a"].GetProperty("promptTokens").GetInt32());
        Assert.Equal("Failed", consoles["b"].GetProperty("outcome").GetString());
        Assert.Contains("HTTP 500", consoles["b"].GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("Skipped", consoles["c"].GetProperty("outcome").GetString());
        Assert.Empty(cluster.Upstreams["c"].Requests);

        var sent = JsonDocument.Parse(cluster.Upstreams["a"].Requests.Single().Body).RootElement;
        Assert.Equal("/v1/chat/completions", cluster.Upstreams["a"].Requests.Single().Path);
        Assert.False(sent.GetProperty("stream").GetBoolean());
        Assert.Equal("0", sent.GetProperty("temperature").GetRawText()); // an integer: old builds stop on 0.0
        Assert.Equal(8, sent.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task The_benchmark_measures_first_token_and_speed_from_the_stream()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        cluster.Upstreams["a"].Handler = Stream;
        using var client = cluster.Factory.CreateClient();

        using var response = await client.PostAsync("/api/inference/pools/chat/benchmark", null);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(run.GetProperty("passed").GetBoolean());
        Assert.Equal("Benchmark", run.GetProperty("kind").GetString());
        var console = run.GetProperty("consoles")[0];
        Assert.Equal("The sky is blue", console.GetProperty("reply").GetString());
        Assert.Equal(55.5, console.GetProperty("promptTokensPerSecond").GetDouble());
        Assert.Equal(7.25, console.GetProperty("generationTokensPerSecond").GetDouble());
        Assert.Equal(4, console.GetProperty("completionTokens").GetInt32());
        Assert.InRange(console.GetProperty("timeToFirstTokenMs").GetDouble(), 0, console.GetProperty("totalMs").GetDouble());
        Assert.True(JsonDocument.Parse(cluster.Upstreams["a"].Requests.Single().Body).RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task A_benchmark_stream_cut_off_before_done_fails_even_with_partial_text_and_timings()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        cluster.Upstreams["a"].Handler = async (context, _) =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"The\"}}]}\n\n");
            await context.Response.WriteAsync("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":1},\"timings\":{\"predicted_per_second\":7.25}}\n\n");
        };
        using var client = cluster.Factory.CreateClient();

        var run = await (await client.PostAsync("/api/inference/pools/chat/benchmark", null)).Content.ReadFromJsonAsync<JsonElement>();

        var console = run.GetProperty("consoles")[0];
        Assert.False(run.GetProperty("passed").GetBoolean());
        Assert.Equal("Failed", console.GetProperty("outcome").GetString());
        Assert.Contains("[DONE]", console.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, console.GetProperty("generationTokensPerSecond").ValueKind);
    }

    [Fact]
    public async Task A_console_with_no_text_in_its_answer_fails_the_check()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        using var client = cluster.Factory.CreateClient(); // the default fake answers JSON without choices

        var run = await (await client.PostAsync("/api/inference/pools/chat/test", null)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(run.GetProperty("passed").GetBoolean());
        Assert.Equal("Failed", run.GetProperty("consoles")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Completion_tokens_without_any_text_still_fail_the_check()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        cluster.Upstreams["a"].Handler = (context, _) => context.Response.WriteAsJsonAsync(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "" } } },
            usage = new { prompt_tokens = 12, completion_tokens = 5 },
        });
        using var client = cluster.Factory.CreateClient();

        var run = await (await client.PostAsync("/api/inference/pools/chat/test", null)).Content.ReadFromJsonAsync<JsonElement>();

        var console = run.GetProperty("consoles")[0];
        Assert.False(run.GetProperty("passed").GetBoolean());
        Assert.Equal("Failed", console.GetProperty("outcome").GetString());
        Assert.Equal(5, console.GetProperty("completionTokens").GetInt32());
    }

    [Fact]
    public async Task The_self_test_runs_one_at_a_time_and_keeps_the_last_result()
    {
        await using var cluster = await Cluster.StartAsync(["a"]);
        var release = new TaskCompletionSource();
        cluster.Upstreams["a"].Handler = async (context, body) =>
        {
            await release.Task;
            await Completion(context, body);
        };
        using var client = cluster.Factory.CreateClient();

        var first = client.PostAsync("/api/inference/pools/chat/test", null);
        await WaitUntilAsync(() => Total(cluster) == 1);
        using var second = await client.PostAsync("/api/inference/pools/chat/benchmark", null);
        release.SetResult();
        using var firstResponse = await first;
        var results = await client.GetFromJsonAsync<JsonElement>("/api/inference/pools/chat/results");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.True(results.GetProperty("check").GetProperty("passed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, results.GetProperty("benchmark").ValueKind);
    }

    [Fact]
    public async Task The_self_test_endpoints_reject_unknown_aliases_and_need_the_token()
    {
        await using var cluster = await Cluster.StartAsync(["a"], token: "s3cret");
        using var client = cluster.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "s3cret");
        using var anonymous = cluster.Factory.CreateClient();

        using var ghostTest = await client.PostAsync("/api/inference/pools/ghost/test", null);
        using var ghostResults = await client.GetAsync("/api/inference/pools/ghost/results");
        using var noToken = await anonymous.PostAsync("/api/inference/pools/chat/test", null);

        Assert.Equal(HttpStatusCode.NotFound, ghostTest.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ghostResults.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);
        Assert.Equal(0, Total(cluster));
    }

    [Fact]
    public async Task The_inference_page_renders_with_and_without_pools()
    {
        await using var on = await Cluster.StartAsync(["a"]);
        await using var off = await Cluster.StartAsync(["a"], pools: false);
        using var onClient = on.Factory.CreateClient();
        using var offClient = off.Factory.CreateClient();

        var enabled = await onClient.GetStringAsync("/inference");
        var disabled = await offClient.GetStringAsync("/inference");

        Assert.Contains("Benchmark", enabled, StringComparison.Ordinal);
        Assert.Contains("qwen.gguf", enabled, StringComparison.Ordinal);
        Assert.Contains("The gateway is off", disabled, StringComparison.Ordinal);
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not met in time.");
            await Task.Delay(25);
        }
    }
}

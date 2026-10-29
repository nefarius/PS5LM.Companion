using System.Net;
using System.Net.Sockets;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Tests.Support;

using Refit;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class LlamaServerProbeTests
{
    private sealed class FakeApi : ILlamaServerApi, IPS5ExporterApi
    {
        public Func<IApiResponse<string>>? Metrics { get; set; }

        public Func<IApiResponse<ModelListResponse>>? Models { get; set; }

        public Task<IApiResponse<string>> GetMetricsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Metrics?.Invoke() ?? throw new HttpRequestException("refused", new SocketException()));

        public Task<IApiResponse<ModelListResponse>> GetModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Models?.Invoke() ?? throw new HttpRequestException("refused"));
    }

    private static IApiResponse<T> Response<T>(HttpStatusCode code, T? content) =>
        new ApiResponse<T>(new HttpResponseMessage(code) { RequestMessage = new HttpRequestMessage() }, content, new RefitSettings());

    [Fact]
    public async Task A_refused_connection_is_closed_not_an_error()
    {
        var result = await new LlamaServerProbe(new FakeApi()).ProbeAsync(CancellationToken.None);

        Assert.Equal(LlamaServerState.Closed, result.State);
    }

    [Fact]
    public async Task Serving_metrics_carry_the_time_the_request_was_sent()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        var api = new FakeApi
        {
            Metrics = () =>
            {
                time.Advance(TimeSpan.FromSeconds(2)); // the answer arrives later than the request was sent
                return Response(HttpStatusCode.OK, "tokens_predicted_total 1\n");
            },
            Models = () => Response(HttpStatusCode.OK, new ModelListResponse([new ModelEntry("m.gguf")])),
        };

        var result = await new LlamaServerProbe(api, time).ProbeAsync(CancellationToken.None);

        Assert.Equal(LlamaServerState.Serving, result.State);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero), result.MetricsObservedAt);
    }

    [Fact]
    public async Task A_503_means_the_model_is_still_loading()
    {
        var api = new FakeApi { Metrics = () => Response<string>(HttpStatusCode.ServiceUnavailable, null) };

        var result = await new LlamaServerProbe(api).ProbeAsync(CancellationToken.None);

        Assert.Equal(LlamaServerState.Loading, result.State);
    }

    [Fact]
    public async Task Metrics_without_llama_counters_are_not_ready_yet()
    {
        var api = new FakeApi { Metrics = () => Response(HttpStatusCode.OK, "unrelated 1") };

        var result = await new LlamaServerProbe(api).ProbeAsync(CancellationToken.None);

        Assert.Equal(LlamaServerState.Loading, result.State);
    }

    [Fact]
    public async Task Serving_returns_the_metrics_and_the_loaded_model()
    {
        var api = new FakeApi
        {
            Metrics = () => Response(HttpStatusCode.OK, FakeLlamaProbe.Metrics(5, 1)),
            Models = () => Response(HttpStatusCode.OK, new ModelListResponse([new ModelEntry("Qwen.gguf")])),
        };

        var result = await new LlamaServerProbe(api).ProbeAsync(CancellationToken.None);

        Assert.Equal(LlamaServerState.Serving, result.State);
        Assert.Equal("Qwen.gguf", result.LoadedModel);
        Assert.Contains("tokens_predicted_total", result.MetricsText);
    }

    [Fact]
    public async Task A_failing_models_endpoint_does_not_break_serving()
    {
        var api = new FakeApi { Metrics = () => Response(HttpStatusCode.OK, FakeLlamaProbe.Metrics(5, 1)) };

        var result = await new LlamaServerProbe(api).ProbeAsync(CancellationToken.None);

        Assert.Equal(LlamaServerState.Serving, result.State);
        Assert.Null(result.LoadedModel);
    }

    [Fact]
    public async Task Exporter_probe_reads_sensors_and_treats_a_missing_exporter_as_absent()
    {
        var present = new FakeApi { Metrics = () => Response(HttpStatusCode.OK, "ps5_temperature_celsius{sensor=\"soc0\"} 64") };

        var sensors = await new ExporterSensorProbe(present).ReadAsync(CancellationToken.None);
        var absent = await new ExporterSensorProbe(new FakeApi()).ReadAsync(CancellationToken.None);

        Assert.Equal(64, sensors?.SocTemperatureC);
        Assert.Null(absent);
    }
}

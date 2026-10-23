using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Tests.Support;

internal sealed class FakeLlamaProbe : ILlamaServerProbe
{
    public LlamaProbeResult Result { get; set; } = LlamaProbeResult.Closed;

    public Task<LlamaProbeResult> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Result);

    public static string Metrics(double tokens, double generationSeconds, int processing = 0) =>
        $"""
        # TYPE llamacpp:tokens_predicted_total counter
        llamacpp:tokens_predicted_total {tokens}
        llamacpp:tokens_predicted_seconds_total {generationSeconds}
        llamacpp:prompt_tokens_total 10
        llamacpp:prompt_seconds_total 1
        llamacpp:requests_processing {processing}
        """;
}

internal sealed class FakeSensorProbe : IConsoleSensorProbe
{
    public ConsoleSensors? Sensors { get; set; }

    public Task<ConsoleSensors?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Sensors);
}

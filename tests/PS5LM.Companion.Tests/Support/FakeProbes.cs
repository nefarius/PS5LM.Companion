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

internal sealed class FakePayloadCatalog : IPayloadCatalog
{
    public PayloadCatalogSnapshot Snapshot { get; set; } = PayloadCatalogSnapshot.NotChecked;

    public int Reads { get; private set; }

    public Task<PayloadCatalogSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(Snapshot);
    }
}

internal sealed class FakePayloadLauncher : IPayloadLauncher
{
    public List<string> Launched { get; } = [];

    public PayloadLaunchResult Result { get; set; } = new(true, 200, "OK");

    public TaskCompletionSource? Entered { get; set; }

    public TaskCompletionSource? Release { get; set; }

    public async Task<PayloadLaunchResult> LaunchAsync(string slot, CancellationToken cancellationToken)
    {
        Entered?.TrySetResult();
        if (Release is not null)
        {
            await Release.Task.WaitAsync(cancellationToken);
        }

        Launched.Add(slot);
        return Result;
    }
}

internal sealed class FakeElfldrProbe : IElfldrProbe
{
    public bool Listening { get; set; }

    public Task<bool> IsListeningAsync(CancellationToken cancellationToken) => Task.FromResult(Listening);
}

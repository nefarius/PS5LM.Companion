using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Telemetry;

/// <summary>
/// Turns llama-server's monotonically increasing counters into speeds, the way the apps' own dashboards do:
/// tokens per second of compute time between two samples.
/// </summary>
public sealed class TelemetryCalculator
{
    private const double MinComputeSeconds = 0.05;

    private double? _lastTokens;
    private double? _lastGenerationSeconds;
    private double? _lastPromptTokens;
    private double? _lastPromptSeconds;

    /// <summary>Updates from a <c>/metrics</c> body; <see langword="null"/> when it holds no llama.cpp counters.</summary>
    public TelemetrySnapshot? Update(string? metricsText, DateTimeOffset now)
    {
        var metrics = PrometheusText.Parse(metricsText);
        var tokens = metrics.Get("tokens_predicted_total");
        if (tokens is null)
        {
            Reset();
            return null;
        }

        var generationSeconds = metrics.Get("tokens_predicted_seconds_total") ?? 0;
        var promptTokens = metrics.Get("prompt_tokens_total") ?? 0;
        var promptSeconds = metrics.Get("prompt_seconds_total") ?? 0;

        var generationSpeed = Speed(tokens.Value, _lastTokens, generationSeconds, _lastGenerationSeconds);
        var promptSpeed = Speed(promptTokens, _lastPromptTokens, promptSeconds, _lastPromptSeconds);

        _lastTokens = tokens;
        _lastGenerationSeconds = generationSeconds;
        _lastPromptTokens = promptTokens;
        _lastPromptSeconds = promptSeconds;

        return new TelemetrySnapshot(
            tokens.Value,
            promptTokens,
            metrics.Get("requests_processing") ?? 0,
            generationSpeed,
            promptSpeed,
            now);
    }

    public void Reset()
    {
        _lastTokens = _lastGenerationSeconds = _lastPromptTokens = _lastPromptSeconds = null;
    }

    private static double Speed(double tokens, double? lastTokens, double seconds, double? lastSeconds)
    {
        if (lastTokens is null || lastSeconds is null)
        {
            return 0;
        }

        var computeSeconds = seconds - lastSeconds.Value;
        return computeSeconds > MinComputeSeconds ? Math.Max(0, (tokens - lastTokens.Value) / computeSeconds) : 0;
    }
}

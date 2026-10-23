using System.Net;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

using Refit;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>
/// Probes llama-server. Connection refusal is the expected answer before a model is loaded, so every
/// transport failure maps to <see cref="LlamaServerState.Closed"/> instead of an error.
/// </summary>
public sealed class LlamaServerProbe(ILlamaServerApi api) : ILlamaServerProbe
{
    public async Task<LlamaProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var metrics = await api.GetMetricsAsync(cancellationToken);

            // Refit reports a transport failure (refused, unreachable) as a response without a status code.
            if (metrics.Error is not null and not ApiException || metrics.StatusCode is null)
            {
                return LlamaProbeResult.Closed;
            }

            if (metrics.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return new LlamaProbeResult(LlamaServerState.Loading, null, null);
            }

            if (!metrics.IsSuccessStatusCode)
            {
                // Something answers but has no metrics (they are optional in llama-server): serving, without telemetry.
                return new LlamaProbeResult(LlamaServerState.Serving, null, await LoadedModelAsync(cancellationToken));
            }

            // While the model loads the server can answer 200 without llama.cpp counters; that is not "ready" yet.
            if (metrics.Content?.Contains("tokens_predicted_total", StringComparison.Ordinal) != true)
            {
                return new LlamaProbeResult(LlamaServerState.Loading, null, null);
            }

            return new LlamaProbeResult(LlamaServerState.Serving, metrics.Content, await LoadedModelAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Refused, unreachable or timed out (Polly's TimeoutRejectedException is an OperationCanceledException).
            return LlamaProbeResult.Closed;
        }
    }

    private async Task<string?> LoadedModelAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var models = await api.GetModelsAsync(cancellationToken);
            return models.IsSuccessStatusCode ? models.Content?.Data?.FirstOrDefault()?.Id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

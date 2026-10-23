using System.Text.Json.Serialization;

using Refit;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>The llama-server the app runs once a model is loaded (port 8081).</summary>
public interface ILlamaServerApi
{
    [Get("/metrics")]
    Task<IApiResponse<string>> GetMetricsAsync(CancellationToken cancellationToken);

    [Get("/v1/models")]
    Task<IApiResponse<ModelListResponse>> GetModelsAsync(CancellationToken cancellationToken);
}

/// <summary>ps5-exporter's Prometheus endpoint (port 9100).</summary>
public interface IPS5ExporterApi
{
    [Get("/metrics")]
    Task<IApiResponse<string>> GetMetricsAsync(CancellationToken cancellationToken);
}

public sealed record ModelListResponse([property: JsonPropertyName("data")] IReadOnlyList<ModelEntry>? Data);

public sealed record ModelEntry([property: JsonPropertyName("id")] string? Id);

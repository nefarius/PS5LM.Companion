using System.Text.Json.Serialization;

using Refit;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>PS5 Payload Manager on port 8084. Launches require <c>X-PLDMGR-Request</c>; reads do not.</summary>
public interface IPayloadManagerApi
{
    [Get("/list_payloads")]
    Task<IApiResponse<PayloadListResponse>> ListPayloadsAsync(CancellationToken cancellationToken);

    [Get("/history_list")]
    Task<IApiResponse<PayloadHistoryResponse>> HistoryAsync(CancellationToken cancellationToken);

    [Get("/autoload_status")]
    Task<IApiResponse<PayloadAutoloadResponse>> AutoloadAsync(CancellationToken cancellationToken);

    [Get("/processes_list")]
    Task<IApiResponse<PayloadProcessesResponse>> ProcessesAsync(CancellationToken cancellationToken);

    [Post("/loadpayload:{slot}")]
    [Headers("X-PLDMGR-Request: 1")]
    Task<IApiResponse<string>> LoadPayloadAsync(string slot, CancellationToken cancellationToken);
}

public sealed record PayloadListResponse([property: JsonPropertyName("items")] IReadOnlyList<PayloadListItem>? Items);

public sealed record PayloadListItem(
    [property: JsonPropertyName("slot")] string? Slot,
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name);

public sealed record PayloadHistoryResponse([property: JsonPropertyName("history")] IReadOnlyList<string>? History);

public sealed record PayloadAutoloadResponse(
    [property: JsonPropertyName("list")] string? List,
    [property: JsonPropertyName("outcomes")] string? Outcomes);

public sealed record PayloadProcessesResponse([property: JsonPropertyName("processes")] IReadOnlyList<PayloadProcess>? Processes);

public sealed record PayloadProcess([property: JsonPropertyName("name")] string? Name);

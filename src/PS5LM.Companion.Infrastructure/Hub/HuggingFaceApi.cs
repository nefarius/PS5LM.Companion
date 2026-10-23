using System.Text.Json.Serialization;

using Refit;

namespace PS5LM.Companion.Infrastructure.Hub;

/// <summary>The parts of the Hugging Face HTTP API the companion needs.</summary>
public interface IHuggingFaceApi
{
    [Get("/api/models")]
    Task<IReadOnlyList<HfModelSummary>> SearchAsync(
        string search,
        string filter,
        string sort,
        string direction,
        int limit,
        CancellationToken cancellationToken);

    [Get("/api/models/{**repo}/tree/main")]
    Task<IReadOnlyList<HfTreeEntry>> TreeAsync(string repo, CancellationToken cancellationToken);
}

public sealed record HfModelSummary(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("downloads")] long Downloads);

public sealed record HfTreeEntry(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("lfs")] HfLfs? Lfs);

public sealed record HfLfs(
    [property: JsonPropertyName("oid")] string? Oid,
    [property: JsonPropertyName("size")] long Size);

/// <summary>Settings for the Hugging Face client. Bound from the <c>HuggingFace</c> section.</summary>
public sealed class HuggingFaceOptions
{
    public const string SectionName = "HuggingFace";

    /// <summary>An access token, needed only for gated or private repositories.</summary>
    public string? Token { get; set; }

    public string BaseUrl { get; set; } = "https://huggingface.co";
}

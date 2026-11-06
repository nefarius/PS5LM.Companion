using System.Text.Json.Serialization;

using PS5LM.Companion.Core.Models.Planning;

using Refit;

namespace PS5LM.Companion.Infrastructure.Hub;

/// <summary>The parts of the Hugging Face HTTP API the companion needs.</summary>
public interface IHuggingFaceApi
{
    [Get("/api/models?expand=gguf&expand=downloads")]
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
    [property: JsonPropertyName("downloads")] long Downloads,
    [property: JsonPropertyName("gguf")] HfGguf? Gguf = null);

/// <summary>The GGUF block Hugging Face adds when a search asks for <c>expand=gguf</c>. The chat template is ignored.</summary>
public sealed record HfGguf(
    [property: JsonPropertyName("total")] long? Total,
    [property: JsonPropertyName("architecture")] string? Architecture,
    [property: JsonPropertyName("context_length")] long? ContextLength);

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

    /// <summary>
    /// GiB assumed free when a console's <c>app.log</c> has no GPU line. 11.38 is the memory PS5LM's planner was calibrated at.
    /// </summary>
    public double DefaultBudgetGib { get; set; } = ModelBudget.CalibratedGib;
}

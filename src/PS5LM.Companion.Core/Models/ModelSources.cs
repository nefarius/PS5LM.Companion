using System.Text.Json;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Models;

/// <summary>Where a model file was downloaded from: a top-level file of a Hugging Face repository.</summary>
/// <param name="Repo">The repository id, e.g. <c>unsloth/Qwen3.8-27B-GGUF</c>.</param>
/// <param name="FileName">The file name inside the repository, equal to the name on the console.</param>
/// <param name="Sha256">The expected checksum when the source recorded one.</param>
/// <param name="SizeBytes">The file size when the source recorded one, otherwise 0.</param>
public sealed record ModelSource(string Repo, string FileName, string? Sha256, long SizeBytes);

/// <summary>
/// Reads the download history PS5LM keeps in <c>downloads.json</c> in its state folder: an array of
/// <c>{repo, file, sha256, bytes, state, save_as}</c> entries. Only finished top-level downloads that kept
/// their repository file name are usable as a source for the same file on another console.
/// </summary>
public static class ModelSources
{
    public const string DownloadsFile = "downloads.json";

    private const int MaxBytes = 1024 * 1024;

    public static async Task<IReadOnlyList<ModelSource>> ReadAsync(IPS5FileSystem fileSystem, StorageRoot stateRoot, CancellationToken cancellationToken) =>
        Parse(await fileSystem.ReadTextAsync($"{stateRoot.FtpPath.TrimEnd('/')}/{DownloadsFile}", MaxBytes, cancellationToken));

    public static IReadOnlyList<ModelSource> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var sources = new List<ModelSource>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && ToSource(entry) is { } source)
                {
                    sources.Add(source);
                }
            }

            return sources;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ModelSource? ToSource(JsonElement entry)
    {
        var repo = Text(entry, "repo");
        var file = Text(entry, "file");
        if (!string.Equals(Text(entry, "state"), "done", StringComparison.OrdinalIgnoreCase)
            || repo is null || file is null
            || !ModelNames.IsValidRepoId(repo) || !ModelNames.IsValidFileName(file))
        {
            return null;
        }

        // A renamed copy is not the repository file, so the same download cannot reproduce it by name.
        var saveAs = Text(entry, "save_as");
        if (!string.IsNullOrEmpty(saveAs) && !string.Equals(saveAs, file, StringComparison.Ordinal))
        {
            return null;
        }

        var size = entry.TryGetProperty("bytes", out var bytes) && bytes.ValueKind == JsonValueKind.Number && bytes.TryGetInt64(out var value) && value > 0
            ? value
            : 0;
        return new ModelSource(repo, file, Text(entry, "sha256") is { Length: > 0 } sha ? sha : null, size);
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

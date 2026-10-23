using System.Net;
using System.Net.Http.Headers;

using PS5LM.Companion.Core.Transfers;

using Refit;

namespace PS5LM.Companion.Infrastructure.Hub;

/// <summary><see cref="IModelHub"/> over Hugging Face: search and listings through Refit, downloads streamed with resume.</summary>
public sealed class HuggingFaceHub(IHuggingFaceApi api, IHttpClientFactory httpClients) : IModelHub
{
    public const string DownloadClientName = "huggingface-download";

    private const int BufferSize = 1 << 20;

    public async Task<IReadOnlyList<HubRepo>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var models = await Hub(() => api.SearchAsync(query, "gguf", "downloads", "-1", 20, cancellationToken));
        return models
            .Where(m => !string.IsNullOrEmpty(m.Id))
            .Select(m => new HubRepo(m.Id!, m.Downloads))
            .ToList();
    }

    public async Task<IReadOnlyList<HubFile>> ListFilesAsync(string repo, CancellationToken cancellationToken)
    {
        var tree = await Hub(() => api.TreeAsync(repo, cancellationToken));
        return tree
            .Where(e => e is { Type: "file", Path: not null } && e.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !e.Path.Contains('/', StringComparison.Ordinal))
            .Select(e => new HubFile(e.Path!, e.Lfs?.Size > 0 ? e.Lfs.Size : e.Size, e.Lfs?.Oid))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<T> Hub<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            throw new HubUnavailableException($"The model hub did not answer: {ex.Message}", ex);
        }
    }

    public async Task DownloadAsync(
        string repo,
        string fileName,
        string destinationPath,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;

        using var http = httpClients.CreateClient(DownloadClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/{repo}/resolve/main/{Uri.EscapeDataString(fileName)}");
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The partial file already holds everything; verification decides whether it is right.
            progress?.Report(existing);
            return;
        }

        response.EnsureSuccessStatusCode();

        // A server that ignores Range answers 200 with the whole body: start over rather than corrupt the file.
        var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        var total = append ? existing : 0;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(
            destinationPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        var buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Report(total);
        }
    }
}

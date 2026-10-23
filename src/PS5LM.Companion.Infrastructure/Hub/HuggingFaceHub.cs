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

    /// <summary>How long a download may receive nothing before it is treated as dropped.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for the response headers of a download request.</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(60);

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
        catch (Exception ex) when (ex is ApiException or ApiRequestException or HttpRequestException or TaskCanceledException or Polly.ExecutionRejectedException)
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

        using var response = await SendWithHeaderTimeoutAsync(http, request, cancellationToken);
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
        while ((read = await ReadWithIdleTimeoutAsync(source, buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            progress?.Report(total);
        }
    }

    private async Task<HttpResponseMessage> SendWithHeaderTimeoutAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headers.CancelAfter(HeaderTimeout);
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"The model hub sent no response within {HeaderTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// Reads with a per-read idle timeout, so a connection that stalls without closing cannot block the single
    /// transfer worker forever. A timeout is an <see cref="IOException"/> (a failed transfer that can be retried),
    /// while the caller's own cancellation still propagates as cancellation.
    /// </summary>
    private async Task<int> ReadWithIdleTimeoutAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(IdleTimeout);
        try
        {
            return await source.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"The download stalled: no data for {IdleTimeout.TotalSeconds:0} seconds.");
        }
    }
}

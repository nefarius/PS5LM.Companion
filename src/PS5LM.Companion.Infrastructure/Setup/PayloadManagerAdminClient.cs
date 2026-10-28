using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.Infrastructure.Setup;

/// <summary>
/// Payload Manager administration over plain <see cref="HttpClient"/>. It speaks the forms that 0.5.x and 0.6.x share
/// (<c>/version</c>, <c>/list_payloads</c>, <c>/manage:upload</c>, <c>/loadpayload:</c>, <c>/shutdown</c>) and the 0.6.x
/// config calls. Every mutating request carries <c>X-PLDMGR-Request: 1</c>, which 0.6.x requires and 0.5.x ignores.
/// </summary>
public sealed class PayloadManagerAdminClient(HttpClient http) : IPayloadManagerAdmin
{
    private const string Marker = "X-PLDMGR-Request";

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("version", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return PayloadManagerVersion.TryParse(text, out _) ? text : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ManagedPayload>> ListPayloadsAsync(CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync("list_payloads", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = new List<ManagedPayload>();
        var root = document.RootElement;
        if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var slot = Text(item, "slot");
                var path = Text(item, "path");
                var fileName = Text(item, "filename") ?? (path is null ? null : path[(path.LastIndexOf('/') + 1)..]);
                if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(fileName))
                {
                    result.Add(new ManagedPayload(slot, Text(item, "id"), fileName, path));
                }
            }
        }
        else if (root.TryGetProperty("payloads", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in paths.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String))
            {
                var path = entry.GetString()!;
                result.Add(new ManagedPayload(string.Empty, null, path[(path.LastIndexOf('/') + 1)..], path));
            }
        }

        return result;
    }

    public async Task UploadAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/manage:upload?filename=" + Uri.EscapeDataString(fileName));
        request.Headers.Add(Marker, "1");
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new("application/octet-stream");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            throw new IOException($"Payload Manager refused the upload of {fileName} ({(int)response.StatusCode}){(body.Length == 0 ? string.Empty : ": " + body[..Math.Min(body.Length, 300)])}.");
        }
    }

    public async Task<PayloadLaunchResult> LoadAsync(string target, CancellationToken cancellationToken)
    {
        // Same shape as the manager's own page (encodeURI): slashes stay, everything else is escaped.
        var encoded = string.Join('/', target.Split('/').Select(Uri.EscapeDataString));
        try
        {
            // 0.6.x only accepts POST for anything that changes state; 0.5.x launched on GET.
            var response = await SendLoadAsync(HttpMethod.Post, encoded, cancellationToken);
            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                response.Dispose();
                response = await SendLoadAsync(HttpMethod.Get, encoded, cancellationToken);
            }

            using var _ = response;
            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            return response.IsSuccessStatusCode
                ? new PayloadLaunchResult(true, (int)response.StatusCode, string.IsNullOrEmpty(body) ? "OK" : body)
                : new PayloadLaunchResult(false, (int)response.StatusCode, string.IsNullOrEmpty(body) ? $"Payload Manager returned {(int)response.StatusCode}." : body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new PayloadLaunchResult(false, null, $"Payload Manager did not answer: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendLoadAsync(HttpMethod method, string encoded, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, "/loadpayload:" + encoded);
        request.Headers.Add(Marker, "1");
        return await http.SendAsync(request, cancellationToken);
    }

    public async Task<IReadOnlySet<string>?> GetProcessesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("processes_list", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("processes", out var processes) || processes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return processes.EnumerateArray()
                .Select(p => Text(p, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<AutoloadConfig?> GetAutoloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("get_config", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var enabled = root.TryGetProperty("AUTOLOAD_ENABLED", out var flag)
                && (flag.ValueKind == JsonValueKind.True || (flag.ValueKind == JsonValueKind.String && flag.GetString() == "true"));
            return new AutoloadConfig(enabled, Text(root, "AUTOLOAD_LIST") ?? string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task SetAutoloadAsync(bool enabled, string list, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "set_config")
        {
            Content = JsonContent.Create(new Dictionary<string, object> { ["AUTOLOAD_ENABLED"] = enabled, ["AUTOLOAD_LIST"] = list }),
        };
        request.Headers.Add(Marker, "1");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"Payload Manager refused the autoload settings ({(int)response.StatusCode}).");
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "shutdown");
        request.Headers.Add(Marker, "1");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode is not HttpStatusCode.OK and not HttpStatusCode.NoContent && !response.IsSuccessStatusCode)
            {
                throw new IOException($"Payload Manager refused to shut down ({(int)response.StatusCode}).");
            }
        }
        catch (HttpRequestException)
        {
            // The manager can drop the connection while it exits; the caller waits for the port to close.
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

using System.Text;
using System.Text.Json;

using Polly.Timeout;

using PS5LM.Companion.Core.Abstractions;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>
/// REST API of the required nefarius VoidShell fork (port 7007 by default): <c>/api/version</c>, <c>/api/library</c>,
/// <c>/api/stats</c> (<c>running_app</c> and <c>firmware</c>) and <c>POST /api/launch</c> with the title ID as plain text. Releases from <c>v1.1.1-nefarius.4</c> also serve
/// <c>GET /api/processes</c> and <c>POST /api/kill</c> (pid as plain text), used to stop a stuck Payload Manager.
/// The reported version stays <c>V1.1</c>, so upstream-shaped stats are still accepted; the fork adds <c>semver</c>
/// to <c>/api/version</c> (absent upstream) to tell itself apart. Transport failures are an unavailable snapshot.
/// </summary>
public sealed class VoidShellClient(HttpClient http) : IAppControlProvider, IProcessControl
{
    public AppProviderKind Kind => AppProviderKind.VoidShell;

    public async Task<AppProviderSnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var versionDoc = await GetJsonAsync("api/version", cancellationToken);
            if (versionDoc is null
                || !versionDoc.RootElement.TryGetProperty("version", out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                return AppProviderSnapshot.Unavailable(Kind, "Port answered but is not VoidShell.");
            }

            var version = versionElement.GetString();
            var semVer = versionDoc.RootElement.TryGetProperty("semver", out var semVerElement)
                && semVerElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(semVerElement.GetString())
                    ? semVerElement.GetString()!.Trim()
                    : null;
            var installed = new List<string>();
            using (var library = await GetJsonAsync("api/library", cancellationToken))
            {
                if (library is null)
                {
                    return AppProviderSnapshot.Unavailable(Kind, "VoidShell's library could not be read.");
                }

                if (!library.RootElement.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
                {
                    return AppProviderSnapshot.Unavailable(Kind, "VoidShell's library could not be read.");
                }

                foreach (var game in games.EnumerateArray())
                {
                    if (game.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var exists = !game.TryGetProperty("exists", out var existsElement) || existsElement.ValueKind != JsonValueKind.False;
                    if (exists && game.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
                    {
                        installed.Add(value);
                    }
                }
            }

            var runningKnown = false;
            string? firmware = null;
            string? active = null;
            using (var stats = await GetJsonAsync("api/stats", cancellationToken))
            {
                if (stats is not null)
                {
                    runningKnown = true;
                    if (stats.RootElement.TryGetProperty("firmware", out var firmwareElement) && firmwareElement.ValueKind == JsonValueKind.String
                        && firmwareElement.GetString() is { Length: > 0 } text)
                    {
                        firmware = text.Trim();
                    }

                    var state = stats.RootElement.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.String
                        ? stateElement.GetString()
                        : null;
                    if (stats.RootElement.TryGetProperty("running_app", out var running) && running.ValueKind == JsonValueKind.String)
                    {
                        // The fork reports the running big app regardless of focus; empty means nothing runs.
                        active = running.GetString();
                    }
                    else if (stats.RootElement.TryGetProperty("active_game", out var game) && game.ValueKind == JsonValueKind.String
                        && (state is null || string.Equals(state, "GAME", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Upstream: active_game keeps the last title after it exits and follows focus, so only
                        // state GAME counts (MENU is the system UI). Without a state field, trust the title.
                        active = game.GetString();
                    }
                }
            }

            return new AppProviderSnapshot(Kind, true, version, installed, runningKnown, active, null, firmware, semVer);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return AppProviderSnapshot.Unavailable(Kind, ex.Message);
        }
    }

    public async Task<AppLaunchResult> LaunchAsync(string titleId, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(titleId, Encoding.UTF8, "text/plain");
            using var response = await http.PostAsync("api/launch", content, cancellationToken);
            return response.IsSuccessStatusCode
                ? new AppLaunchResult(true, true, "VoidShell queued the title; it does not confirm the launch succeeded.")
                : new AppLaunchResult(true, false, $"HTTP {(int)response.StatusCode}.");
        }
        catch (TimeoutRejectedException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The POST may already be on the wire. Not dispatched would make the launcher try websrv as well.
            return new AppLaunchResult(true, false, ex.Message);
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return new AppLaunchResult(false, false, ex.Message);
        }
    }

    /// <summary><c>GET /api/processes</c>; <see langword="null"/> when the answer is missing, which is how an older fork looks (404).</summary>
    public async Task<IReadOnlyList<RemoteProcess>?> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var document = await GetJsonAsync("api/processes", cancellationToken);
            if (document is null
                || !document.RootElement.TryGetProperty("processes", out var processes)
                || processes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var result = new List<RemoteProcess>();
            foreach (var process in processes.EnumerateArray())
            {
                if (process.ValueKind == JsonValueKind.Object
                    && process.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id)
                    && process.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    result.Add(new RemoteProcess(id, name.GetString() ?? string.Empty));
                }
            }

            return result;
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken))
        {
            return null;
        }
    }

    /// <summary><c>POST /api/kill</c> with the pid as plain text; true only for <c>{"status":"ok"}</c>.</summary>
    public async Task<bool> KillAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(pid.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8, "text/plain");
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/kill") { Content = content };
            // The fork refuses a kill without this header, which keeps a web page from sending one cross-origin.
            request.Headers.Add("X-VoidShell-Request", "1");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() == "ok";
        }
        catch (Exception ex) when (IsTransient(ex, cancellationToken) || ex is JsonException)
        {
            return false;
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Connection failures and probe timeouts. <see cref="TimeoutRejectedException"/> does not derive from
    /// <see cref="OperationCanceledException"/>, so it has to be named. A cancelled call still propagates:
    /// Polly only raises the timeout when the caller's token was not already cancelled.
    /// </summary>
    internal static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && ex is HttpRequestException or IOException or TimeoutRejectedException or OperationCanceledException;
}

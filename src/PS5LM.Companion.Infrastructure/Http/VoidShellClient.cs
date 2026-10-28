using System.Text;
using System.Text.Json;

using Polly.Timeout;

using PS5LM.Companion.Core.Abstractions;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>
/// VoidShell's REST API (port 7007): <c>/api/version</c>, <c>/api/library</c>, <c>/api/stats</c> (<c>running_app</c> in the nefarius fork) and
/// <c>POST /api/launch</c> with the title ID as a plain-text body. Transport failures are an unavailable snapshot.
/// </summary>
public sealed class VoidShellClient(HttpClient http) : IAppControlProvider
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
            var installed = new List<string>();
            using (var library = await GetJsonAsync("api/library", cancellationToken))
            {
                if (library is not null && library.RootElement.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array)
                {
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

            return new AppProviderSnapshot(Kind, true, version, installed, runningKnown, active, null, firmware);
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

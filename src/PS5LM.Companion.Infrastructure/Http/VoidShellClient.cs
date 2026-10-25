using System.Text;
using System.Text.Json;

using PS5LM.Companion.Core.Abstractions;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>
/// VoidShell's REST API (port 7007): <c>/api/version</c>, <c>/api/library</c>, <c>/api/stats</c> and
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
            if (versionDoc is null || !versionDoc.RootElement.TryGetProperty("version", out var versionElement))
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
                        var exists = !game.TryGetProperty("exists", out var existsElement) || existsElement.ValueKind != JsonValueKind.False;
                        if (exists && game.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
                        {
                            installed.Add(value);
                        }
                    }
                }
            }

            var runningKnown = false;
            string? active = null;
            using (var stats = await GetJsonAsync("api/stats", cancellationToken))
            {
                if (stats is not null)
                {
                    runningKnown = true;
                    var state = stats.RootElement.TryGetProperty("state", out var stateElement) && stateElement.ValueKind == JsonValueKind.String
                        ? stateElement.GetString()
                        : null;
                    // active_game keeps the last title after it exits; only state GAME means it is in the foreground
                    // (MENU is the system UI). Without a state field, trust the title.
                    if (stats.RootElement.TryGetProperty("active_game", out var game) && game.ValueKind == JsonValueKind.String
                        && (state is null || string.Equals(state, "GAME", StringComparison.OrdinalIgnoreCase)))
                    {
                        active = game.GetString();
                    }
                }
            }

            return new AppProviderSnapshot(Kind, true, version, installed, runningKnown, active, null);
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
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool IsTransient(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested;
}

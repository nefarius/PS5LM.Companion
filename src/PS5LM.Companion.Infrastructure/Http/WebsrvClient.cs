using System.Text.Json;

using PS5LM.Companion.Core.Abstractions;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>
/// ps5-payload-dev websrv (port 8080): <c>/version</c> identifies it, <c>/fs/user/app/?fmt=json</c> lists installed
/// titles and <c>/launch?titleId=</c> starts one. It has no foreground-title API, so running state is unknown.
/// </summary>
public sealed class WebsrvClient(HttpClient http) : IAppControlProvider
{
    private static readonly string[] TitleFolders = ["fs/user/app/?fmt=json", "fs/user/appmeta/?fmt=json"];

    public AppProviderKind Kind => AppProviderKind.Websrv;

    public async Task<AppProviderSnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? version;
            using (var response = await http.GetAsync("version", cancellationToken))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return AppProviderSnapshot.Unavailable(Kind, $"HTTP {(int)response.StatusCode}.");
                }

                try
                {
                    using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("tag", out var tag))
                    {
                        return AppProviderSnapshot.Unavailable(Kind, "Port answered but is not websrv.");
                    }

                    version = tag.ToString();
                }
                catch (JsonException)
                {
                    return AppProviderSnapshot.Unavailable(Kind, "Port answered but is not websrv.");
                }
            }

            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in TitleFolders)
            {
                foreach (var name in await ListDirectoriesAsync(folder, cancellationToken))
                {
                    installed.Add(name);
                }
            }

            return new AppProviderSnapshot(Kind, true, version, installed.ToList(), false, null, null);
        }
        catch (Exception ex) when (VoidShellClient.IsTransient(ex, cancellationToken))
        {
            return AppProviderSnapshot.Unavailable(Kind, ex.Message);
        }
    }

    public async Task<AppLaunchResult> LaunchAsync(string titleId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync($"launch?titleId={Uri.EscapeDataString(titleId)}", cancellationToken);
            return (int)response.StatusCode switch
            {
                >= 200 and < 300 => new AppLaunchResult(true, true, "websrv started the title (it stops the running app first)."),
                503 => new AppLaunchResult(true, false, "the console refused to start the title."),
                var code => new AppLaunchResult(true, false, $"HTTP {code}."),
            };
        }
        catch (Exception ex) when (VoidShellClient.IsTransient(ex, cancellationToken))
        {
            return new AppLaunchResult(false, false, ex.Message);
        }
    }

    private async Task<List<string>> ListDirectoriesAsync(string path, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        using var response = await http.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return names;
        }

        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return names;
            }

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } value)
                {
                    names.Add(value);
                }
            }
        }
        catch (JsonException)
        {
        }

        return names;
    }
}

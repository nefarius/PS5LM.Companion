namespace PS5LM.Companion.App;

/// <summary>Probes the app's own <c>/health</c> endpoint for the container HEALTHCHECK.</summary>
internal static class HealthProbe
{
    public static async Task<int> RunAsync()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8080";
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries)[0]
            .Replace("0.0.0.0", "localhost", StringComparison.Ordinal)
            .Replace("+", "localhost", StringComparison.Ordinal)
            .Replace("*", "localhost", StringComparison.Ordinal);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await client.GetAsync(new Uri(new Uri(first), "/health"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return 1;
        }
    }
}

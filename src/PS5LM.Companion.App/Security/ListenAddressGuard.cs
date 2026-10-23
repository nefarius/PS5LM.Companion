using System.Net;

namespace PS5LM.Companion.App.Security;

/// <summary>Refuses to expose an unauthenticated companion on the network by accident.</summary>
internal static class ListenAddressGuard
{
    public const string DefaultUrls = "http://localhost:5000";

    /// <summary>Checks every place ASP.NET Core can take a listener from, not just <c>urls</c>.</summary>
    public static void EnsureSafe(IConfiguration configuration, CompanionOptions options) =>
        EnsureSafe(ListenUrls(configuration), options);

    public static void EnsureSafe(string? urls, CompanionOptions options)
    {
        if (!string.IsNullOrEmpty(options.AccessToken) || options.AllowAnonymous || IsLoopbackOnly(urls))
        {
            return;
        }

        throw new InvalidOperationException(
            "The companion listens on a non-loopback address without authentication. "
            + "Set Companion:AccessToken, or Companion:AllowAnonymous=true for a trusted network.");
    }

    /// <summary>
    /// Collects the <c>urls</c> setting (also <c>ASPNETCORE_URLS</c>), the <c>HTTP_PORTS</c>/<c>HTTPS_PORTS</c> settings
    /// (ports only, so every interface) and <c>Kestrel:Endpoints:*:Url</c> into one semicolon-separated list.
    /// Returns <see langword="null"/> when nothing is configured, which means the loopback default.
    /// </summary>
    public static string? ListenUrls(IConfiguration configuration)
    {
        var urls = new List<string>();

        if (configuration["urls"] is { Length: > 0 } configured)
        {
            urls.Add(configured);
        }

        foreach (var (key, scheme) in new[] { ("HTTP_PORTS", "http"), ("HTTPS_PORTS", "https") })
        {
            if (configuration[key] is { Length: > 0 } ports)
            {
                urls.AddRange(ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(port => $"{scheme}://+:{port}"));
            }
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            if (endpoint["Url"] is { Length: > 0 } url)
            {
                urls.Add(url);
            }
        }

        return urls.Count == 0 ? null : string.Join(';', urls);
    }

    public static bool IsLoopbackOnly(string? urls)
    {
        foreach (var url in (string.IsNullOrWhiteSpace(urls) ? DefaultUrls : urls).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // Wildcard hosts ("+", "*") do not parse as URIs; they always mean "every interface".
            var candidate = url.Replace("+", "wildcard", StringComparison.Ordinal).Replace("*", "wildcard", StringComparison.Ordinal);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var loopback = uri.IsLoopback
                           || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
            if (!loopback)
            {
                return false;
            }
        }

        return true;
    }
}

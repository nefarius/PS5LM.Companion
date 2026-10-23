using System.Net;

namespace PS5LM.Companion.App.Security;

/// <summary>Refuses to expose an unauthenticated companion on the network by accident.</summary>
internal static class ListenAddressGuard
{
    public const string DefaultUrls = "http://localhost:5000";

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

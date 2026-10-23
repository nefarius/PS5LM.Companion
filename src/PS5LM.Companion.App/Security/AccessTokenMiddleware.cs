using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace PS5LM.Companion.App.Security;

/// <summary>
/// Shared-secret gate for the dashboard and the REST API. The token is accepted as a bearer token (automation)
/// or in an HttpOnly cookie set by the login form (browsers). <c>/health</c> stays open for container probes.
/// </summary>
internal sealed class AccessTokenMiddleware(RequestDelegate next, IOptions<CompanionOptions> options)
{
    public const string CookieName = "ps5lm_token";
    public const string LoginPath = "/login";

    private readonly byte[]? _token = string.IsNullOrEmpty(options.Value.AccessToken)
        ? null
        : Encoding.UTF8.GetBytes(options.Value.AccessToken);

    public async Task InvokeAsync(HttpContext context)
    {
        if (_token is null || IsOpen(context.Request.Path) || IsAuthorized(context.Request))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)
            || context.Request.Path.StartsWithSegments("/_blazor", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        context.Response.Redirect(LoginPath);
    }

    /// <summary>Checks a candidate in constant time.</summary>
    public static bool Matches(string? candidate, string expected) =>
        candidate is not null
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    private static bool IsOpen(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.Ordinal)
        || path.StartsWithSegments(LoginPath, StringComparison.Ordinal);

    private bool IsAuthorized(HttpRequest request)
    {
        var expected = Encoding.UTF8.GetString(_token!);

        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && Matches(header["Bearer ".Length..].Trim(), expected))
        {
            return true;
        }

        return request.Cookies.TryGetValue(CookieName, out var cookie) && Matches(cookie, expected);
    }
}

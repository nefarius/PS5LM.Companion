using System.Net;

using Microsoft.Extensions.Options;

using PS5LM.Companion.App.Components;

namespace PS5LM.Companion.App.Security;

internal static class LoginEndpoints
{
    public static void MapLogin(this IEndpointRouteBuilder app)
    {
        app.MapGet(AccessTokenMiddleware.LoginPath, (HttpContext context) =>
            Results.Content(Page(error: false), "text/html"));

        app.MapPost(AccessTokenMiddleware.LoginPath, async (HttpContext context, IOptions<CompanionOptions> options) =>
        {
            var form = await context.Request.ReadFormAsync();
            var expected = options.Value.AccessToken;
            if (string.IsNullOrEmpty(expected) || !AccessTokenMiddleware.Matches(form["token"], expected))
            {
                return Results.Content(Page(error: true), "text/html", statusCode: StatusCodes.Status401Unauthorized);
            }

            context.Response.Cookies.Append(
                AccessTokenMiddleware.CookieName,
                expected,
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = context.Request.IsHttps,
                    Expires = DateTimeOffset.UtcNow.AddDays(30),
                });
            return Results.Redirect("/");
        });
    }

    private static string Page(bool error) => $$"""
        <!DOCTYPE html>
        <html lang="en"><head><meta charset="utf-8"><title>{{AppPageTitle.Prefix}}Sign in</title>
        <meta name="viewport" content="width=device-width, initial-scale=1"></head>
        <body style="font-family:sans-serif;max-width:22rem;margin:4rem auto">
        <h1>PS5LM Companion</h1>
        {{(error ? "<p style=\"color:#c00\">Wrong access token.</p>" : string.Empty)}}
        <form method="post" action="{{WebUtility.HtmlEncode(AccessTokenMiddleware.LoginPath)}}">
        <input type="password" name="token" placeholder="Access token" autofocus style="width:100%;padding:.5rem">
        <button type="submit" style="margin-top:.5rem;padding:.5rem 1rem">Sign in</button>
        </form></body></html>
        """;
}

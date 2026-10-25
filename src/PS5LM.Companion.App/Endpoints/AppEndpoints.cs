using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.App.Endpoints;

/// <summary>PS5LM and PS5LM Plus as VoidShell or websrv reported them in the latest status check.</summary>
public sealed class GetAppsEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest<AppControlStatus>
{
    public override void Configure()
    {
        Get("/consoles/{consoleId}/apps");
        AllowAnonymous();
        Description(b => b.WithTags("Apps"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.OkAsync(console.Store.Current.AppControl ?? AppControlStatus.NotChecked, ct);
    }
}

/// <summary>Launches <c>ps5lm</c> or <c>ps5lmplus</c> through VoidShell (preferred) or websrv.</summary>
public sealed class LaunchAppEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/apps/{variant}/launch");
        AllowAnonymous();
        Description(b => b.WithTags("Apps"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        var text = Route<string>("variant");
        if (string.IsNullOrEmpty(text) || char.IsDigit(text[0]) || !Enum.TryParse<PS5Variant>(text, true, out var variant) || variant == PS5Variant.Unknown)
        {
            await Send.ResultAsync(ApiResults.Error("The variant must be ps5lm or ps5lmplus.", StatusCodes.Status400BadRequest));
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await console.Apps.LaunchAsync(variant, ct))));
    }
}

/// <summary>Asks <c>ps5lm</c> or <c>ps5lmplus</c> to quit cleanly through its <c>quit</c> command file.</summary>
public sealed class CloseAppEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/apps/{variant}/close");
        AllowAnonymous();
        Description(b => b.WithTags("Apps"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        var text = Route<string>("variant");
        if (string.IsNullOrEmpty(text) || char.IsDigit(text[0]) || !Enum.TryParse<PS5Variant>(text, true, out var variant) || variant == PS5Variant.Unknown)
        {
            await Send.ResultAsync(ApiResults.Error("The variant must be ps5lm or ps5lmplus.", StatusCodes.Status400BadRequest));
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await console.Closer.CloseAsync(variant, ct))));
    }
}

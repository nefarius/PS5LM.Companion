using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.App.Endpoints;

public sealed class GetComponentUpdatesEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest<ComponentUpdateStatus>
{
    public override void Configure()
    {
        Get("/consoles/{consoleId}/updates");
        AllowAnonymous();
        Description(b => b.WithTags("Status"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.OkAsync(console.Store.Current.Updates ?? ComponentUpdateStatus.Empty, ct);
    }
}

public sealed class CheckComponentUpdatesEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest<ComponentUpdateStatus>
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/updates/check");
        AllowAnonymous();
        Description(b => b.WithTags("Status"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        if (console.Updates is null)
        {
            await Send.ResultAsync(ApiResults.Error("The pinned setup manifest is not available, so components cannot be compared.", StatusCodes.Status503ServiceUnavailable));
            return;
        }

        await Send.OkAsync(await console.Updates.CheckAsync(ct), ct);
    }
}

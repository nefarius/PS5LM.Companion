using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.App.Endpoints;

public sealed record SetAutoLoadRequest(bool AutoLoad, string? DefaultModel);

public sealed class GetAutoLoadEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/consoles/{consoleId}/app-settings/auto-load");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () =>
        {
            var read = await console.AutoLoad.GetAsync(ct);
            return read.Settings is { } settings
                ? Results.Ok(settings)
                : ApiResults.From(new OperationResult(read.Outcome, read.Message));
        }));
    }
}

public sealed class SetAutoLoadEndpoint(IConsoleRegistry registry) : Endpoint<SetAutoLoadRequest>
{
    public override void Configure()
    {
        Put("/consoles/{consoleId}/app-settings/auto-load");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(SetAutoLoadRequest req, CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () =>
            ApiResults.From(await console.AutoLoad.SetAsync(req.AutoLoad, req.DefaultModel, ct))));
    }
}

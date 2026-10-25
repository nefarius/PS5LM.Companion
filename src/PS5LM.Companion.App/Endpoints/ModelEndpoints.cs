using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;

namespace PS5LM.Companion.App.Endpoints;

public sealed record ModelsResponse(IReadOnlyList<ModelFile> Models, IReadOnlyList<string> UploadTargets);

public sealed class GetModelsEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/consoles/{consoleId}/models");
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
        var installation = console.Status.LastInstallation;
        if (installation?.StateRoot is null)
        {
            return ApiResults.Error(installation?.Message ?? "The console is not reachable.", StatusCodes.Status503ServiceUnavailable);
        }

        var models = await console.Models.ListAsync(installation, console.Store.Current.LoadedModel, ct);
        var targets = await console.Models.UploadTargetsAsync(installation, ct);
        return Results.Ok(new ModelsResponse(models, targets.Select(t => t.Name).ToList()));
    }));
    }
}

public sealed record LoadModelRequest(string Path);

public sealed class LoadModelEndpoint(IConsoleRegistry registry) : Endpoint<LoadModelRequest>
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/models/load");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(LoadModelRequest req, CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await console.Control.LoadAsync(req.Path, ct))));
    }
}

public sealed class UnloadModelEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/models/unload");
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

        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await console.Control.UnloadAsync(ct))));
    }
}

public sealed record DeleteModelRequest(string Path);

public sealed class DeleteModelEndpoint(IConsoleRegistry registry, ModelDeletionService deletion) : Endpoint<DeleteModelRequest>
{
    public override void Configure()
    {
        Delete("/consoles/{consoleId}/models");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(DeleteModelRequest req, CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await deletion.DeleteAsync(console, req.Path, ct))));
    }
}

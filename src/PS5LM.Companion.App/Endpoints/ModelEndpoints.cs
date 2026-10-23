using FastEndpoints;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.App.Endpoints;

public sealed record ModelsResponse(IReadOnlyList<ModelFile> Models, IReadOnlyList<string> UploadTargets);

public sealed class GetModelsEndpoint(CompanionStatusService status, CompanionStatusStore store, ModelCatalog catalog)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/models");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(CancellationToken ct) => await Send.ResultAsync(await ApiResults.GuardAsync(async () =>
    {
        var installation = status.LastInstallation;
        if (installation?.StateRoot is null)
        {
            return ApiResults.Error(installation?.Message ?? "The console is not reachable.", StatusCodes.Status503ServiceUnavailable);
        }

        var models = await catalog.ListAsync(installation, store.Current.LoadedModel, ct);
        var targets = await catalog.UploadTargetsAsync(installation, ct);
        return Results.Ok(new ModelsResponse(models, targets.Select(t => t.Name).ToList()));
    }));
}

public sealed record LoadModelRequest(string Path);

public sealed class LoadModelEndpoint(ModelControlService control) : Endpoint<LoadModelRequest>
{
    public override void Configure()
    {
        Post("/models/load");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(LoadModelRequest req, CancellationToken ct) =>
        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await control.LoadAsync(req.Path, ct))));
}

public sealed class UnloadModelEndpoint(ModelControlService control) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/models/unload");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await control.UnloadAsync(ct))));
}

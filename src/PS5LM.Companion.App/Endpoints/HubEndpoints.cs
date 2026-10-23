using FastEndpoints;

using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Endpoints;

public sealed record SearchRequest(string? Q);

public sealed class SearchHubEndpoint(IModelHub hub) : Endpoint<SearchRequest>
{
    public override void Configure()
    {
        Get("/hub/search");
        AllowAnonymous();
        Description(b => b.WithTags("Hub"));
    }

    public override async Task HandleAsync(SearchRequest req, CancellationToken ct) =>
        await Send.ResultAsync(await ApiResults.GuardAsync(async () => Results.Ok(await hub.SearchAsync(req.Q ?? string.Empty, ct))));
}

public sealed record HubFilesRequest(string? Repo);

public sealed class GetHubFilesEndpoint(IModelHub hub) : Endpoint<HubFilesRequest>
{
    public override void Configure()
    {
        Get("/hub/files");
        AllowAnonymous();
        Description(b => b.WithTags("Hub"));
    }

    public override async Task HandleAsync(HubFilesRequest req, CancellationToken ct) =>
        await Send.ResultAsync(await ApiResults.GuardAsync(async () =>
            ModelNames.IsValidRepoId(req.Repo)
                ? Results.Ok(await hub.ListFilesAsync(req.Repo!, ct))
                : ApiResults.Error("A repository id such as owner/name is required.", StatusCodes.Status400BadRequest)));
}

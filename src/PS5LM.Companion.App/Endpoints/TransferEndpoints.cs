using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Endpoints;

public sealed class GetTransfersEndpoint(IConsoleRegistry registry, TransferService transfers) : EndpointWithoutRequest<IReadOnlyList<Transfer>>
{
    public override void Configure()
    {
        Get("/consoles/{consoleId}/transfers");
        AllowAnonymous();
        Description(b => b.WithTags("Transfers"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.OkAsync(transfers.Snapshot(console.Id), ct);
    }
}

/// <summary>The body of a transfer request; the console comes from the route.</summary>
public sealed record CreateTransferRequest(string Repo, string FileName, string? TargetRoot);

public sealed class CreateTransferEndpoint(IConsoleRegistry registry, TransferService transfers) : Endpoint<CreateTransferRequest>
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/transfers");
        AllowAnonymous();
        Description(b => b.WithTags("Transfers"));
    }

    public override async Task HandleAsync(CreateTransferRequest req, CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () =>
        {
            var (result, transfer) = await transfers.EnqueueAsync(new TransferRequest(console.Id, req.Repo, req.FileName, req.TargetRoot), ct);
            return result.Succeeded && transfer is not null
                ? Results.Json(transfer, statusCode: StatusCodes.Status202Accepted)
                : ApiResults.From(result);
        }));
    }
}

public sealed class CancelTransferEndpoint(IConsoleRegistry registry, TransferService transfers) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/consoles/{consoleId}/transfers/{id}");
        AllowAnonymous();
        Description(b => b.WithTags("Transfers"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        // A transfer of another console is not visible here, so it cannot be cancelled through this route.
        if (!Guid.TryParse(Route<string>("id"), out var id) || !transfers.Snapshot(console.Id).Any(t => t.Id == id))
        {
            await Send.ResultAsync(ApiResults.Error("That transfer does not exist on this console.", StatusCodes.Status404NotFound));
            return;
        }

        await Send.ResultAsync(ApiResults.From(await transfers.CancelAsync(id, ct)));
    }
}

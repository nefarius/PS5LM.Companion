using FastEndpoints;

using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Endpoints;

public sealed class GetTransfersEndpoint(TransferService transfers) : EndpointWithoutRequest<IReadOnlyList<Transfer>>
{
    public override void Configure()
    {
        Get("/transfers");
        AllowAnonymous();
        Description(b => b.WithTags("Transfers"));
    }

    public override Task HandleAsync(CancellationToken ct) => Send.OkAsync(transfers.Snapshot(), ct);
}

public sealed class CreateTransferEndpoint(TransferService transfers) : Endpoint<TransferRequest>
{
    public override void Configure()
    {
        Post("/transfers");
        AllowAnonymous();
        Description(b => b.WithTags("Transfers"));
    }

    public override async Task HandleAsync(TransferRequest req, CancellationToken ct) =>
        await Send.ResultAsync(await ApiResults.GuardAsync(async () =>
        {
            var (result, transfer) = await transfers.EnqueueAsync(req, ct);
            return result.Succeeded && transfer is not null
                ? Results.Json(transfer, statusCode: StatusCodes.Status202Accepted)
                : ApiResults.From(result);
        }));
}

public sealed record CancelTransferRequest(Guid Id);

public sealed class CancelTransferEndpoint(TransferService transfers) : Endpoint<CancelTransferRequest>
{
    public override void Configure()
    {
        Delete("/transfers/{id}");
        AllowAnonymous();
        Description(b => b.WithTags("Transfers"));
    }

    public override async Task HandleAsync(CancelTransferRequest req, CancellationToken ct) =>
        await Send.ResultAsync(ApiResults.From(await transfers.CancelAsync(req.Id, ct)));
}

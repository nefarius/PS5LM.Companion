using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Endpoints;

public sealed record CreateModelCopyRequest(IReadOnlyList<string>? Paths, IReadOnlyList<CreateModelCopyTarget>? Targets);

public sealed record CreateModelCopyTarget(string? ConsoleId, string? TargetRoot);

public sealed record ModelCopyTargetResponse(
    string ConsoleId,
    string TargetRoot,
    CopyTargetOutcome Outcome,
    Guid? TransferId,
    string? Message,
    TransferState? TransferState);

public sealed record ModelCopyItemResponse(
    Guid CopyItemId,
    string SourcePath,
    string FileName,
    long SizeBytes,
    CopyItemState State,
    long StagedBytes,
    string? Sha256,
    string? Error,
    IReadOnlyList<ModelCopyTargetResponse> Targets);

public sealed record ModelCopyResponse(
    Guid BatchId,
    string SourceConsoleId,
    CopyBatchState State,
    IReadOnlyList<ModelCopyItemResponse> Items);

public sealed class CreateModelCopyEndpoint(IConsoleRegistry registry, TransferService transfers) : Endpoint<CreateModelCopyRequest>
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/model-copies");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(CreateModelCopyRequest req, CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () =>
        {
            var targets = (req.Targets ?? []).Select(target => new CopyTargetRequest(target.ConsoleId ?? string.Empty, target.TargetRoot)).ToList();
            var paths = (req.Paths ?? []).Where(path => path is not null).ToList();
            var result = await transfers.EnqueueCopiesAsync(new CopyRequest(console.Id, paths, targets), ct);
            if (result.Batch is null)
            {
                return ApiResults.From(new(result.Outcome, result.Message));
            }

            var status = result.Outcome == OperationOutcome.Accepted
                ? StatusCodes.Status202Accepted
                : StatusCodes.Status200OK;
            return Results.Json(Map(result.Batch, result.Items, transfers), statusCode: status);
        }));
    }

    internal static ModelCopyResponse Map(CopyBatch batch, IReadOnlyList<CopyItem> items, TransferService transfers) =>
        new(
            batch.Id,
            batch.SourceConsoleId,
            batch.State,
            items.Select(item => new ModelCopyItemResponse(
                item.Id,
                item.SourceAppPath,
                item.FileName,
                item.SizeBytes,
                item.State,
                item.StagedBytes,
                item.Sha256,
                item.Error,
                item.Targets.Select(target => new ModelCopyTargetResponse(
                    target.ConsoleId,
                    target.TargetRoot,
                    target.Outcome,
                    target.TransferId,
                    target.Message,
                    target.TransferId is Guid id ? transfers.Snapshot().FirstOrDefault(transfer => transfer.Id == id)?.State : null)).ToList())).ToList());
}

public sealed class GetModelCopyEndpoint(TransferService transfers) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/model-copies/{batchId}");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(Route<string>("batchId"), out var batchId) || transfers.FindCopy(batchId) is not { } copy)
        {
            await Send.ResultAsync(ApiResults.Error("That copy does not exist.", StatusCodes.Status404NotFound));
            return;
        }

        await Send.OkAsync(CreateModelCopyEndpoint.Map(copy.Batch, copy.Items, transfers), ct);
    }
}

public sealed class CancelModelCopyEndpoint(TransferService transfers) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/model-copies/{batchId}");
        AllowAnonymous();
        Description(b => b.WithTags("Models"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(Route<string>("batchId"), out var batchId))
        {
            await Send.ResultAsync(ApiResults.Error("That copy does not exist.", StatusCodes.Status404NotFound));
            return;
        }

        await Send.ResultAsync(ApiResults.From(await transfers.CancelCopyAsync(batchId, ct)));
    }
}

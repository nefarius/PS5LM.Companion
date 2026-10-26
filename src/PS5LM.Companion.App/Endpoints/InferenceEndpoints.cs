using FastEndpoints;

using PS5LM.Companion.App.Inference;
using PS5LM.Companion.Core.Inference;

namespace PS5LM.Companion.App.Endpoints;

public sealed record InferencePoolsResponse(bool Enabled, IReadOnlyList<PoolSnapshot> Pools);

/// <summary>
/// Which consoles serve each public model alias right now and why the others do not.
/// Use it to check whether the gateway can answer before sending work.
/// </summary>
public sealed class GetInferencePoolsEndpoint(InferenceRouter router) : EndpointWithoutRequest<InferencePoolsResponse>
{
    public override void Configure()
    {
        Get("/inference/pools");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new InferencePoolsResponse(router.Enabled, router.Snapshot()), ct);
}

public sealed record SelfTestResults(SelfTestRun? Check, SelfTestRun? Benchmark);

internal static class SelfTestRoutes
{
    public static async Task RunAsync(InferenceSelfTest tests, string? alias, SelfTestKind kind, Func<IResult, Task> send, Func<SelfTestRun, Task> ok, CancellationToken ct)
    {
        var (start, run) = await tests.RunAsync(alias ?? string.Empty, kind, ct);
        switch (start)
        {
            case SelfTestStart.UnknownAlias:
                await send(ApiResults.Error($"The model '{alias}' is not served by this gateway.", StatusCodes.Status404NotFound));
                break;
            case SelfTestStart.AlreadyRunning:
                await send(ApiResults.Error($"A test of '{alias}' is already running.", StatusCodes.Status409Conflict));
                break;
            default:
                await ok(run!);
                break;
        }
    }
}

/// <summary>Sends a tiny request to every ready console of the pool and reports which ones answered.</summary>
public sealed class RunInferenceTestEndpoint(InferenceSelfTest tests) : EndpointWithoutRequest<SelfTestRun>
{
    public override void Configure()
    {
        Post("/inference/pools/{alias}/test");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SelfTestRoutes.RunAsync(tests, Route<string>("alias"), SelfTestKind.Check, r => Send.ResultAsync(r), run => Send.OkAsync(run, ct), ct);
}

/// <summary>Streams a fixed prompt through every ready console of the pool and measures first-token time and tokens per second.</summary>
public sealed class RunInferenceBenchmarkEndpoint(InferenceSelfTest tests) : EndpointWithoutRequest<SelfTestRun>
{
    public override void Configure()
    {
        Post("/inference/pools/{alias}/benchmark");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override Task HandleAsync(CancellationToken ct) =>
        SelfTestRoutes.RunAsync(tests, Route<string>("alias"), SelfTestKind.Benchmark, r => Send.ResultAsync(r), run => Send.OkAsync(run, ct), ct);
}

/// <summary>The latest test and benchmark of a pool since the companion started.</summary>
public sealed class GetInferenceTestResultsEndpoint(InferenceRouter router, InferenceSelfTest tests) : EndpointWithoutRequest<SelfTestResults>
{
    public override void Configure()
    {
        Get("/inference/pools/{alias}/results");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var alias = Route<string>("alias") ?? string.Empty;
        if (!router.HasAlias(alias))
        {
            await Send.ResultAsync(ApiResults.Error($"The model '{alias}' is not served by this gateway.", StatusCodes.Status404NotFound));
            return;
        }

        await Send.OkAsync(new SelfTestResults(tests.Last(alias, SelfTestKind.Check), tests.Last(alias, SelfTestKind.Benchmark)), ct);
    }
}

public sealed record InferencePoolBody
{
    public string Alias { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public List<string> Consoles { get; set; } = [];

    public List<string>? AlternateNames { get; set; }
}

/// <summary>A new <c>Alias</c> different from the route alias renames the pool.</summary>
public sealed record InferencePoolSettings(string Model, List<string> Consoles, [property: System.Text.Json.Serialization.JsonPropertyName("alias")] string? NewAlias = null, List<string>? AlternateNames = null);

public sealed class CreateInferencePoolEndpoint(IInferencePoolSource pools) : Endpoint<InferencePoolBody>
{
    public override void Configure()
    {
        Post("/inference/pools");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override async Task HandleAsync(InferencePoolBody request, CancellationToken ct) =>
        await Send.ResultAsync(PoolEdits.From(pools.Add(request.Alias, request.Model, request.Consoles, request.AlternateNames), created: true));
}

public sealed class UpdateInferencePoolEndpoint(IInferencePoolSource pools) : Endpoint<InferencePoolSettings>
{
    public override void Configure()
    {
        Put("/inference/pools/{alias}");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override async Task HandleAsync(InferencePoolSettings request, CancellationToken ct) =>
        await Send.ResultAsync(PoolEdits.From(pools.Update(Route<string>("alias") ?? "", request.Model, request.Consoles, request.NewAlias, request.AlternateNames)));
}

public sealed class DeleteInferencePoolEndpoint(IInferencePoolSource pools) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/inference/pools/{alias}");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override async Task HandleAsync(CancellationToken ct) =>
        await Send.ResultAsync(PoolEdits.From(pools.Remove(Route<string>("alias") ?? ""), removed: true));
}

/// <summary>
/// Queues a model download on every console of the pool that lacks it. The optional <c>model</c> query
/// parameter names any file a pool member has; without it the pool's own model is used.
/// </summary>
public sealed class SyncInferencePoolEndpoint(PoolSyncService sync) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/inference/pools/{alias}/sync");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await sync.SyncAsync(Route<string>("alias") ?? "", Query<string>("model", isRequired: false), ct);
        await Send.ResultAsync(result.Status switch
        {
            PoolSyncStatus.Ok => Results.Ok(result),
            PoolSyncStatus.NotFound => ApiResults.Error(result.Message ?? "That pool is not configured.", StatusCodes.Status404NotFound),
            _ => ApiResults.Error(result.Message ?? "Nothing to sync.", StatusCodes.Status409Conflict),
        });
    }
}

/// <summary>Every model file found on the pool's consoles and which consoles have it.</summary>
public sealed class ListInferencePoolModelsEndpoint(PoolSyncService sync) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/inference/pools/{alias}/models");
        AllowAnonymous();
        Description(b => b.WithTags("Inference"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var result = await sync.ListAsync(Route<string>("alias") ?? "", ct);
        await Send.ResultAsync(result.Status == PoolSyncStatus.NotFound
            ? ApiResults.Error("That pool is not configured.", StatusCodes.Status404NotFound)
            : Results.Ok(result.Models));
    }
}

internal static class PoolEdits
{
    public static IResult From(PoolEditResult result, bool created = false, bool removed = false) => result.Status switch
    {
        PoolEditStatus.Ok when removed => Results.NoContent(),
        PoolEditStatus.Ok when created => Results.Created($"/api/inference/pools/{result.Pool!.Alias}", result.Pool),
        PoolEditStatus.Ok => Results.Ok(result.Pool),
        PoolEditStatus.Invalid => ApiResults.Error(result.Message ?? "Invalid pool.", StatusCodes.Status400BadRequest),
        PoolEditStatus.Conflict or PoolEditStatus.ReadOnly => ApiResults.Error(result.Message ?? "That pool cannot be changed.", StatusCodes.Status409Conflict),
        _ => ApiResults.Error(result.Message ?? "That pool is not configured.", StatusCodes.Status404NotFound),
    };
}

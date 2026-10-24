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

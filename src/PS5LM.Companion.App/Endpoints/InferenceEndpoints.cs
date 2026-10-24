using FastEndpoints;

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

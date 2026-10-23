using FastEndpoints;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.App.Endpoints;

/// <summary>The normalised console status, for automation. The dashboard reads the same store directly.</summary>
public sealed class GetStatusEndpoint(CompanionStatusStore store) : EndpointWithoutRequest<CompanionStatus>
{
    public override void Configure()
    {
        Get("/status");
        AllowAnonymous();
        Description(b => b.WithTags("Status"));
    }

    public override Task HandleAsync(CancellationToken ct) => Send.OkAsync(store.Current, ct);
}

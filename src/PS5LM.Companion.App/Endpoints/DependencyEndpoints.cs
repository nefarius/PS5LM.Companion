using FastEndpoints;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.App.Endpoints;

/// <summary>Payload rows from the latest status check, for automation that does not want the whole console status.</summary>
public sealed record DependencyOverview(bool PayloadManagerAvailable, string? Message, IReadOnlyList<DependencyStatus> Dependencies);

public sealed class GetDependenciesEndpoint(CompanionStatusStore store) : EndpointWithoutRequest<DependencyOverview>
{
    public override void Configure()
    {
        Get("/dependencies");
        AllowAnonymous();
        Description(b => b.WithTags("Status"));
    }

    public override Task HandleAsync(CancellationToken ct)
    {
        var current = store.Current;
        return Send.OkAsync(new DependencyOverview(current.PayloadManagerAvailable, current.PayloadManagerMessage, current.Dependencies), ct);
    }
}

public sealed class LaunchDependencyEndpoint(DependencyLaunchService launcher) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/dependencies/{id}/launch");
        AllowAnonymous();
        Description(b => b.WithTags("Status"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<string>("id");
        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await launcher.LaunchAsync(id, ct))));
    }
}

using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.App.Endpoints;

/// <summary>Payload rows from the latest status check, for automation that does not want the whole console status.</summary>
public sealed record DependencyOverview(bool PayloadManagerAvailable, string? Message, IReadOnlyList<DependencyStatus> Dependencies);

public sealed class GetDependenciesEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest<DependencyOverview>
{
    public override void Configure()
    {
        Get("/consoles/{consoleId}/dependencies");
        AllowAnonymous();
        Description(b => b.WithTags("Status"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        var current = console.Store.Current;
        await Send.OkAsync(new DependencyOverview(current.PayloadManagerAvailable, current.PayloadManagerMessage, current.Dependencies), ct);
    }
}

public sealed class LaunchDependencyEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/dependencies/{id}/launch");
        AllowAnonymous();
        Description(b => b.WithTags("Status"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        var id = Route<string>("id");
        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await console.Dependencies.LaunchAsync(id, ct))));
    }
}

/// <summary>Stops a stuck Payload Manager (through VoidShell when it will not exit) and starts the pinned release through elfldr.</summary>
public sealed class RestartPayloadManagerEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/consoles/{consoleId}/payload-manager/restart");
        AllowAnonymous();
        Description(b => b.WithTags("Status"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.ResultAsync(await ApiResults.GuardAsync(async () => ApiResults.From(await console.ManagerRestart.RestartAsync(ct))));
    }
}

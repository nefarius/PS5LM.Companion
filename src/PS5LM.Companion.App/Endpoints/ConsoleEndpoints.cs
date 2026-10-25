using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.App.Endpoints;

/// <summary>Who a console is, without its connection details beyond the host.</summary>
public sealed record ConsoleSummary(string Id, string Name, string Host, ConsoleSource Source, CompanionLifecycle Lifecycle);

/// <summary>The normalised status of one console, with the console it belongs to.</summary>
public sealed record ConsoleStatusResponse(ConsoleSummary Console, CompanionStatus Status);

internal static class ConsoleRoutes
{
    public static ConsoleSummary Summary(IConsoleRegistry registry, ConsoleRuntime console) =>
        new(console.Id, console.Name, console.Host, console.Source, console.Store.Current.Lifecycle);

    /// <summary>The console named by the <c>consoleId</c> route value, or a 404 answer when there is none.</summary>
    public static ConsoleRuntime? Find(IConsoleRegistry registry, string? consoleId, out IResult? notFound)
    {
        var console = registry.Find(consoleId);
        notFound = console is null ? ApiResults.Error($"The console '{consoleId}' is not configured.", StatusCodes.Status404NotFound) : null;
        return console;
    }
}

public sealed class GetConsolesEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest<IReadOnlyList<ConsoleSummary>>
{
    public override void Configure()
    {
        Get("/consoles");
        AllowAnonymous();
        Description(b => b.WithTags("Consoles"));
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(registry.All.Select(console => ConsoleRoutes.Summary(registry, console)).ToList(), ct);
}

/// <summary>The normalised console status, for automation. The dashboard reads the same store directly.</summary>
public sealed class GetStatusEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest<ConsoleStatusResponse>
{
    public override void Configure()
    {
        Get("/consoles/{consoleId}/status");
        AllowAnonymous();
        Description(b => b.WithTags("Consoles"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (ConsoleRoutes.Find(registry, Route<string>("consoleId"), out var notFound) is not { } console)
        {
            await Send.ResultAsync(notFound!);
            return;
        }

        await Send.OkAsync(new ConsoleStatusResponse(ConsoleRoutes.Summary(registry, console), console.Store.Current), ct);
    }
}

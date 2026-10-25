using FastEndpoints;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Endpoints;

public sealed class ConsoleSettingsBody
{
    public string? DisplayName { get; set; }

    public string Host { get; set; } = string.Empty;

    public int FtpPort { get; set; } = 2121;

    public int LlamaPort { get; set; } = 8081;

    public int ExporterPort { get; set; } = 9100;

    public int ElfldrPort { get; set; } = 9021;

    public string? PayloadManagerUrl { get; set; }

    public int VoidShellPort { get; set; } = 7007;

    public int WebsrvPort { get; set; } = 8080;

    public PS5Variant Variant { get; set; }

    public int PollIntervalSeconds { get; set; } = 3;

    public int DiscoveryCacheSeconds { get; set; } = 30;

    public int LoadGraceSeconds { get; set; } = 600;

    public PS5Options ToOptions() => new()
    {
        DisplayName = DisplayName,
        Host = Host,
        FtpPort = FtpPort,
        LlamaPort = LlamaPort,
        ExporterPort = ExporterPort,
        ElfldrPort = ElfldrPort,
        PayloadManagerUrl = PayloadManagerUrl,
        VoidShellPort = VoidShellPort,
        WebsrvPort = WebsrvPort,
        Variant = Variant,
        PollIntervalSeconds = PollIntervalSeconds,
        DiscoveryCacheSeconds = DiscoveryCacheSeconds,
        LoadGraceSeconds = LoadGraceSeconds,
    };

    public static ConsoleSettingsBody From(PS5Options options) => new()
    {
        DisplayName = options.DisplayName,
        Host = options.Host,
        FtpPort = options.FtpPort,
        LlamaPort = options.LlamaPort,
        ExporterPort = options.ExporterPort,
        ElfldrPort = options.ElfldrPort,
        PayloadManagerUrl = options.PayloadManagerUrl,
        VoidShellPort = options.VoidShellPort,
        WebsrvPort = options.WebsrvPort,
        Variant = options.Variant,
        PollIntervalSeconds = options.PollIntervalSeconds,
        DiscoveryCacheSeconds = options.DiscoveryCacheSeconds,
        LoadGraceSeconds = options.LoadGraceSeconds,
    };
}

public sealed record CreateConsoleRequest
{
    public string Id { get; set; } = string.Empty;

    public ConsoleSettingsBody Settings { get; set; } = new();
}

public sealed record ConsoleSettingsResponse(string Id, ConsoleSource Source, ConsoleSettingsBody Settings);

internal static class ConsoleEdits
{
    public static IResult Result(ConsoleEditResult result, IConsoleRegistry registry, bool removed = false)
    {
        return result.Status switch
        {
            ConsoleEditStatus.Ok when removed => Results.NoContent(),
            ConsoleEditStatus.Ok => Results.Ok(ConsoleRoutes.Summary(registry, result.Console!)),
            ConsoleEditStatus.Invalid => ApiResults.Error(result.Message ?? "Invalid console settings.", StatusCodes.Status400BadRequest),
            ConsoleEditStatus.Conflict or ConsoleEditStatus.ReadOnly => ApiResults.Error(result.Message ?? "That console cannot be changed.", StatusCodes.Status409Conflict),
            _ => ApiResults.Error(result.Message ?? "That console is not configured.", StatusCodes.Status404NotFound),
        };
    }
}

public sealed class CreateConsoleEndpoint(IConsoleRegistry registry) : Endpoint<CreateConsoleRequest>
{
    public override void Configure()
    {
        Post("/consoles");
        AllowAnonymous();
        Description(b => b.WithTags("Consoles"));
    }

    public override async Task HandleAsync(CreateConsoleRequest request, CancellationToken ct)
    {
        var result = registry.Add(request.Id ?? "", (request.Settings ?? new ConsoleSettingsBody()).ToOptions());
        var status = result.Status == ConsoleEditStatus.Ok ? StatusCodes.Status201Created : 0;
        if (status == StatusCodes.Status201Created)
        {
            await Send.ResultAsync(Results.Created($"/api/consoles/{result.Console!.Id}", ConsoleRoutes.Summary(registry, result.Console)));
            return;
        }

        await Send.ResultAsync(ConsoleEdits.Result(result, registry));
    }
}

public sealed class UpdateConsoleEndpoint(IConsoleRegistry registry, TransferService transfers) : Endpoint<ConsoleSettingsBody>
{
    public override void Configure()
    {
        Put("/consoles/{consoleId}");
        AllowAnonymous();
        Description(b => b.WithTags("Consoles"));
    }

    public override async Task HandleAsync(ConsoleSettingsBody request, CancellationToken ct)
    {
        var id = Route<string>("consoleId") ?? "";
        if (transfers.HasActive(id))
        {
            await Send.ResultAsync(ApiResults.Error("Cancel this console's transfers before changing it.", StatusCodes.Status409Conflict));
            return;
        }

        var result = registry.Update(id, request.ToOptions());
        await Send.ResultAsync(ConsoleEdits.Result(result, registry));
    }
}

public sealed class DeleteConsoleEndpoint(IConsoleRegistry registry, TransferService transfers) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Delete("/consoles/{consoleId}");
        AllowAnonymous();
        Description(b => b.WithTags("Consoles"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var id = Route<string>("consoleId") ?? "";
        if (transfers.HasActive(id))
        {
            await Send.ResultAsync(ApiResults.Error("Cancel this console's transfers before removing it.", StatusCodes.Status409Conflict));
            return;
        }

        await Send.ResultAsync(ConsoleEdits.Result(registry.Remove(id), registry, removed: true));
    }
}

public sealed record DiscoverConsolesRequest
{
    public string? Cidr { get; set; }
}

public sealed class DiscoverConsolesEndpoint(IConsoleDiscovery discovery) : Endpoint<DiscoverConsolesRequest, IReadOnlyList<DiscoveredConsole>>
{
    public override void Configure()
    {
        Post("/consoles/discover");
        AllowAnonymous();
        Description(b => b.WithTags("Consoles"));
    }

    public override async Task HandleAsync(DiscoverConsolesRequest request, CancellationToken ct)
    {
        try
        {
            await Send.OkAsync(await discovery.ScanAsync(request.Cidr, ct), ct);
        }
        catch (FormatException ex)
        {
            await Send.ResultAsync(ApiResults.Error(ex.Message, StatusCodes.Status400BadRequest));
        }
    }
}

public sealed class TestConsoleEndpoint(IConsoleProbe probe) : Endpoint<ConsoleSettingsBody, ConsoleProbeReport>
{
    public override void Configure()
    {
        Post("/consoles/test");
        AllowAnonymous();
        Description(b => b.WithTags("Consoles"));
    }

    public override async Task HandleAsync(ConsoleSettingsBody request, CancellationToken ct)
    {
        var errors = ConsoleSettings.Errors("ok", request.ToOptions());
        if (errors.Count > 0)
        {
            await Send.ResultAsync(ApiResults.Error(string.Join(" ", errors), StatusCodes.Status400BadRequest));
            return;
        }

        await Send.OkAsync(await probe.TestAsync(request.ToOptions(), ct), ct);
    }
}

public sealed class GetConsoleSettingsEndpoint(IConsoleRegistry registry) : EndpointWithoutRequest<ConsoleSettingsResponse>
{
    public override void Configure()
    {
        Get("/consoles/{consoleId}/settings");
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

        await Send.OkAsync(new ConsoleSettingsResponse(console.Id, console.Source, ConsoleSettingsBody.From(console.Options)), ct);
    }
}

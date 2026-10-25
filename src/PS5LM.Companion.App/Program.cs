using System.Text.Json.Serialization;

using FastEndpoints;

using Microsoft.AspNetCore.DataProtection;

using MudBlazor.Services;

using PS5LM.Companion.App.Inference;
using PS5LM.Companion.App.Monitoring;
using PS5LM.Companion.App.Security;
using PS5LM.Companion.App.Setup;
using PS5LM.Companion.Infrastructure;

using Serilog;
using Serilog.Events;

namespace PS5LM.Companion.App;

public sealed class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Container HEALTHCHECK: the aspnet runtime image ships neither curl nor wget.
        if (args.Contains("--health-check"))
        {
            return await HealthProbe.RunAsync();
        }

        // One-shot console preparation; never starts the web host.
        if (args.Length > 0 && args[0] == "setup")
        {
            return await SetupCommand.RunAsync(args[1..]);
        }

        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateBootstrapLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Host.UseSerilog((context, services, configuration) => configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .WriteTo.Console());

            var companion = builder.Configuration.GetSection(CompanionOptions.SectionName).Get<CompanionOptions>() ?? new();
            ListenAddressGuard.EnsureSafe(builder.Configuration, companion);

            builder.Services
                .AddOptions<CompanionOptions>()
                .Bind(builder.Configuration.GetSection(CompanionOptions.SectionName))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // IResult-based endpoints use the minimal-API serializer, not FastEndpoints': keep enums readable in both.
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

            // Keep the antiforgery/cookie keys in the data volume so a container restart does not invalidate open pages.
            builder.Services.AddDataProtection()
                .SetApplicationName("PS5LM.Companion")
                .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(companion.DataDirectory, "keys")));
            builder.Services.AddHealthChecks();
            builder.Services.AddFastEndpoints();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddMudServices();
            builder.Services.AddPS5Connectivity(builder.Configuration);
            builder.Services.AddModelManagement(builder.Configuration);
            builder.Services.AddInferenceRouting(builder.Configuration);
            builder.Services.AddHttpForwarder();
            builder.Services.AddSingleton<InferenceUpstream>();
            builder.Services.AddSingleton<InferenceSelfTest>();
            // Open Blazor circuits and SignalR sockets never end on their own, so the default 30 s grace period
            // is always spent in full. Nothing here needs more than a few seconds to stop.
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));
            builder.Services.AddHostedService<StatusMonitor>();
            builder.Services.AddHostedService<TransferWorker>();

            var app = builder.Build();

            app.UseSerilogRequestLogging(options => options.GetLevel = RequestLogLevel);
            app.UseMiddleware<AccessTokenMiddleware>();
            app.UseAntiforgery();
            app.MapStaticAssets();
            app.MapHealthChecks("/health");
            app.MapLogin();
            app.UseFastEndpoints(config =>
            {
                config.Endpoints.RoutePrefix = "api";
                config.Serializer.Options.Converters.Add(new JsonStringEnumConverter());
            });

            app.MapInferenceGateway();

            app.MapRazorComponents<Components.App>().AddInteractiveServerRenderMode();

            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "PS5LM Companion terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    /// <summary>
    /// Container probes hit <c>/health</c> every few seconds. Successful probes stay below the default
    /// Information minimum; failures and every other request keep Serilog's usual levels.
    /// </summary>
    private static LogEventLevel RequestLogLevel(HttpContext context, double _, Exception? exception) =>
        exception is null
        && context.Response.StatusCode < StatusCodes.Status500InternalServerError
        && context.Request.Path.StartsWithSegments("/health")
            ? LogEventLevel.Verbose
            : exception is not null || context.Response.StatusCode > 499
                ? LogEventLevel.Error
                : LogEventLevel.Information;
}

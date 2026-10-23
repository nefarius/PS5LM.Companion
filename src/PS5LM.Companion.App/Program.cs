using System.Text.Json.Serialization;

using FastEndpoints;

using MudBlazor.Services;

using PS5LM.Companion.App.Monitoring;
using PS5LM.Companion.App.Security;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Infrastructure;

using Serilog;

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
            builder.Services.AddHealthChecks();
            builder.Services.AddFastEndpoints();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddMudServices();
            builder.Services.AddPS5Connectivity(builder.Configuration);
            builder.Services.AddModelManagement(builder.Configuration);
            builder.Services.AddSingleton<CompanionStatusStore>();
            builder.Services.AddHostedService<StatusMonitor>();
            builder.Services.AddHostedService<TransferWorker>();

            var app = builder.Build();

            app.UseSerilogRequestLogging();
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
}

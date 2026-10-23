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

            builder.Services.AddHealthChecks();

            var app = builder.Build();

            app.UseSerilogRequestLogging();
            app.MapHealthChecks("/health");

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

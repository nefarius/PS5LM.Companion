using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Infrastructure.Ftp;
using PS5LM.Companion.Infrastructure.Http;

using Polly;

using Refit;

namespace PS5LM.Companion.Infrastructure;

public static class ServiceCollectionExtensions
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Registers FTP access, the llama-server and ps5-exporter probes, and the status service.</summary>
    public static IServiceCollection AddPS5Connectivity(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<PS5Options>()
            .Bind(configuration.GetSection(PS5Options.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PS5Options>>().Value);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IPS5FileSystem, FtpFileSystem>();

        services
            .AddRefitClient<ILlamaServerApi>()
            .ConfigureHttpClient((sp, client) => client.BaseAddress = BaseAddress(sp, o => o.LlamaPort))
            .AddResilienceHandler("llama-probe", builder => builder.AddTimeout(ProbeTimeout));
        services
            .AddRefitClient<IPS5ExporterApi>()
            .ConfigureHttpClient((sp, client) => client.BaseAddress = BaseAddress(sp, o => o.ExporterPort))
            .AddResilienceHandler("exporter-probe", builder => builder.AddTimeout(ProbeTimeout));

        services.AddSingleton<ILlamaServerProbe, LlamaServerProbe>();
        services.AddSingleton<IConsoleSensorProbe, ExporterSensorProbe>();

        services.AddSingleton<LoadTracker>();
        services.AddSingleton<CompanionStatusService>();
        return services;
    }

    private static Uri BaseAddress(IServiceProvider sp, Func<PS5Options, int> port)
    {
        var options = sp.GetRequiredService<PS5Options>();
        return new UriBuilder(Uri.UriSchemeHttp, options.Host, port(options)).Uri;
    }
}

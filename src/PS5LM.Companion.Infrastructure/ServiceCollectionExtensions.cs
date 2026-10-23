using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Infrastructure.Ftp;
using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Infrastructure.Hub;
using PS5LM.Companion.Infrastructure.Persistence;

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
            .Validate(static options => string.IsNullOrWhiteSpace(options.PayloadManagerUrl) || IsHttpUrl(options.PayloadManagerUrl),
                "PS5:PayloadManagerUrl must be an absolute http or https URL.")
            .ValidateOnStart();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PS5Options>>().Value);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<IPS5FileSystem, FtpFileSystem>();

        services
            .AddRefitGeneratedClient<ILlamaServerApi>()
            .ConfigureHttpClient((sp, client) => client.BaseAddress = BaseAddress(sp, o => o.LlamaPort))
            .AddResilienceHandler("llama-probe", builder => builder.AddTimeout(ProbeTimeout));
        services
            .AddRefitGeneratedClient<IPS5ExporterApi>()
            .ConfigureHttpClient((sp, client) => client.BaseAddress = BaseAddress(sp, o => o.ExporterPort))
            .AddResilienceHandler("exporter-probe", builder => builder.AddTimeout(ProbeTimeout));

        services
            .AddRefitGeneratedClient<IPayloadManagerApi>()
            .ConfigureHttpClient((sp, client) => client.BaseAddress = sp.GetRequiredService<PS5Options>().PayloadManagerBaseAddress())
            .AddResilienceHandler("payload-manager", builder => builder.AddTimeout(ProbeTimeout));

        services.AddSingleton<ILlamaServerProbe, LlamaServerProbe>();
        services.AddSingleton<IConsoleSensorProbe, ExporterSensorProbe>();
        services.AddSingleton<PayloadManagerClient>();
        services.AddSingleton<IPayloadCatalog>(sp => sp.GetRequiredService<PayloadManagerClient>());
        services.AddSingleton<IPayloadLauncher>(sp => sp.GetRequiredService<PayloadManagerClient>());
        services.AddSingleton<IElfldrProbe, TcpElfldrProbe>();

        services.AddSingleton<LoadTracker>();
        services.AddSingleton<CompanionStatusService>();
        services.AddSingleton<DependencyLaunchService>();
        return services;
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Registers model listing, load/unload, the Hugging Face hub and the transfer pipeline.</summary>
    public static IServiceCollection AddModelManagement(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HuggingFaceOptions>(configuration.GetSection(HuggingFaceOptions.SectionName));
        services.AddSingleton(sp =>
        {
            // Resolved lazily so host-level overrides (tests, containers) of Companion:DataDirectory apply.
            var dataDirectory = sp.GetRequiredService<IConfiguration>()["Companion:DataDirectory"] ?? "data";
            return new TransferOptions
            {
                CacheDirectory = Path.Combine(dataDirectory, "cache"),
                StateFile = Path.Combine(dataDirectory, "transfers.json"),
            };
        });

        services.AddTransient<HuggingFaceAuthHandler>();
        services
            .AddRefitGeneratedClient<IHuggingFaceApi>()
            .ConfigureHttpClient((sp, client) => client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<HuggingFaceOptions>>().Value.BaseUrl))
            .AddHttpMessageHandler<HuggingFaceAuthHandler>()
            .AddStandardResilienceHandler();

        // Model files are gigabytes: no overall timeout and no retry pipeline, the transfer service resumes instead.
        services
            .AddHttpClient(HuggingFaceHub.DownloadClientName, (sp, client) =>
            {
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<HuggingFaceOptions>>().Value.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddHttpMessageHandler<HuggingFaceAuthHandler>();

        services.AddSingleton<IModelHub, HuggingFaceHub>();
        services.AddSingleton<ITransferStore, JsonTransferStore>();
        services.AddSingleton<ModelCatalog>();
        services.AddSingleton<ModelControlService>();
        services.AddSingleton<TransferService>();
        return services;
    }

    private static Uri BaseAddress(IServiceProvider sp, Func<PS5Options, int> port)
    {
        var options = sp.GetRequiredService<PS5Options>();
        return new UriBuilder(Uri.UriSchemeHttp, options.Host, port(options)).Uri;
    }
}

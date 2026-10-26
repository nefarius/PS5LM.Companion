using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Inference;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Infrastructure.Hub;
using PS5LM.Companion.Infrastructure.Persistence;

using Polly;

using Refit;

namespace PS5LM.Companion.Infrastructure;

public static class ServiceCollectionExtensions
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Registers the configured consoles and the clients each one needs.</summary>
    public static IServiceCollection AddPS5Connectivity(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<ConsolesOptions>()
            .Bind(configuration.GetSection(ConsolesOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ConsolesOptions>, ConsolesOptionsValidator>();
        services.AddSingleton(TimeProvider.System);

        // One pipeline per probe kind; ConsoleConnectorFactory sets the base address for each console.
        services
            .AddHttpClient(ConsoleConnectorFactory.LlamaClient)
            .AddResilienceHandler("llama-probe", builder => builder.AddTimeout(ProbeTimeout));
        services
            .AddHttpClient(ConsoleConnectorFactory.ExporterClient)
            .AddResilienceHandler("exporter-probe", builder => builder.AddTimeout(ProbeTimeout));
        services
            .AddHttpClient(ConsoleConnectorFactory.PayloadManagerClient)
            .AddResilienceHandler("payload-manager", builder => builder.AddTimeout(ProbeTimeout));

        services
            .AddHttpClient(ConsoleConnectorFactory.VoidShellHttpClient)
            .AddResilienceHandler("voidshell", builder => builder.AddTimeout(ProbeTimeout));
        services
            .AddHttpClient(ConsoleConnectorFactory.WebsrvHttpClient)
            .AddResilienceHandler("websrv", builder => builder.AddTimeout(ProbeTimeout));

        services.AddSingleton<IConsoleConnectorFactory, ConsoleConnectorFactory>();
        services.AddSingleton<IConsoleProbe, ConsoleConnectionProbe>();
        services.AddSingleton<IConsoleDiscovery, LanConsoleScanner>();
        services.AddSingleton<IConsoleStore>(sp =>
        {
            var dataDirectory = sp.GetRequiredService<IConfiguration>()["Companion:DataDirectory"] ?? "data";
            return new JsonConsoleStore(Path.Combine(dataDirectory, "consoles.json"), sp.GetRequiredService<ILogger<JsonConsoleStore>>());
        });
        services.AddSingleton<IConsoleRegistry>(sp => new ConsoleRegistry(
            sp.GetRequiredService<IOptions<ConsolesOptions>>().Value,
            sp.GetRequiredService<IConsoleConnectorFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IConsoleStore>(),
            message => sp.GetRequiredService<ILogger<ConsoleRegistry>>().LogWarning("{Message}", message)));
        return services;
    }

    /// <summary>Registers the model-aware inference router. It is inert unless <c>Inference:Pools</c> is configured.</summary>
    public static IServiceCollection AddInferenceRouting(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<InferenceOptions>()
            .Bind(configuration.GetSection(InferenceOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<InferenceOptions>, InferenceOptionsValidator>();
        services.AddSingleton<IInferencePoolSource>(sp =>
        {
            var dataDirectory = sp.GetRequiredService<IConfiguration>()["Companion:DataDirectory"] ?? "data";
            var consoles = sp.GetRequiredService<IConsoleRegistry>();
            var store = new JsonInferencePoolStore(
                Path.Combine(dataDirectory, "inference-pools.json"),
                sp.GetRequiredService<ILogger<JsonInferencePoolStore>>(),
                consoles);
            return new HybridInferencePools(
                sp.GetRequiredService<IOptions<InferenceOptions>>().Value,
                consoles,
                store,
                message => sp.GetRequiredService<ILogger<HybridInferencePools>>().LogWarning("{Message}", message));
        });
        services.AddSingleton(sp => new InferenceRouter(
            sp.GetRequiredService<IConsoleRegistry>(),
            sp.GetRequiredService<IOptions<InferenceOptions>>().Value,
            sp.GetRequiredService<IInferencePoolSource>()));
        return services;
    }

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
        services.AddSingleton<TransferService>();
        services.AddSingleton<ModelDeletionService>();
        return services;
    }
}

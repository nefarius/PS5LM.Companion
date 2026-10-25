using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddSingleton<IConsoleConnectorFactory, ConsoleConnectorFactory>();
        services.AddSingleton<IConsoleRegistry>(sp => new ConsoleRegistry(
            sp.GetRequiredService<IOptions<ConsolesOptions>>().Value,
            sp.GetRequiredService<IConsoleConnectorFactory>(),
            sp.GetRequiredService<TimeProvider>()));
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
        services.AddSingleton(sp => new InferenceRouter(
            sp.GetRequiredService<IConsoleRegistry>(),
            sp.GetRequiredService<IOptions<InferenceOptions>>().Value));
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

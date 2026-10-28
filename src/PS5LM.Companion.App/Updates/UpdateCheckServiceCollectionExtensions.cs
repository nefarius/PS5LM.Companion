using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PS5LM.Companion.App.Updates;

internal static class UpdateCheckServiceCollectionExtensions
{
    /// <summary>Registers a process-wide check against the latest stable GitHub Release.</summary>
    public static IServiceCollection AddUpdateCheck(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAssemblyVersionSource, AssemblyVersionSource>();
        services.AddHttpClient(LatestReleaseChecker.HttpClientName, LatestReleaseChecker.ConfigureClient);
        services.AddSingleton(static provider =>
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            return new LatestReleaseChecker(
                factory.CreateClient(LatestReleaseChecker.HttpClientName),
                provider.GetRequiredService<IAssemblyVersionSource>(),
                provider.GetRequiredService<TimeProvider>());
        });
        return services;
    }
}

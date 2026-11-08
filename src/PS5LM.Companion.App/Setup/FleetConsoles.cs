using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Infrastructure.Persistence;

namespace PS5LM.Companion.App.Setup;

/// <summary>
/// Consoles <c>update --all-consoles</c> walks: configuration <c>PS5:Consoles</c> first, then user consoles stored
/// in <c>consoles.json</c>. A stored console whose id is already in configuration is skipped, as the registry does.
/// </summary>
internal static class FleetConsoles
{
    public static IReadOnlyList<(string Id, PS5Options Options)> Load(IConfiguration configuration, string dataDirectory)
    {
        var bound = new ConsolesOptions();
        configuration.GetSection(ConsolesOptions.SectionName).Bind(bound);
        var consoles = new List<(string Id, PS5Options Options)>();
        foreach (var (key, options) in bound.Consoles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.Host))
            {
                continue;
            }

            consoles.Add((key.ToLowerInvariant(), options));
        }

        var stored = new JsonConsoleStore(Path.Combine(dataDirectory, "consoles.json"), NullLogger<JsonConsoleStore>.Instance).Load();
        foreach (var console in stored)
        {
            if (consoles.Any(item => item.Id.Equals(console.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            consoles.Add((console.Id, console.Options));
        }

        return consoles;
    }
}

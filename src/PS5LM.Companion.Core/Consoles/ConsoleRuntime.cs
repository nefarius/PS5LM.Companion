using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>
/// One managed console and all state that belongs to it: status, load tracking, model control and payload
/// launches. Nothing here is shared with another console, so a slow or dead console cannot affect the others.
/// </summary>
public sealed class ConsoleRuntime : IDisposable
{
    public ConsoleRuntime(string id, PS5Options options, ConsoleConnectors connectors, TimeProvider time, ConsoleSource source)
    {
        Id = id;
        Options = options;
        Source = source;
        Name = string.IsNullOrWhiteSpace(options.DisplayName) ? id : options.DisplayName.Trim();
        FileSystem = connectors.FileSystem;
        Store = new CompanionStatusStore();
        LoadTracker = new LoadTracker(time);
        Status = new CompanionStatusService(options, connectors.FileSystem, connectors.Llama, connectors.Sensors, connectors.Payloads, connectors.Elfldr, LoadTracker, time, connectors.AppControl);
        Models = new ModelCatalog(connectors.FileSystem);
        Control = new ModelControlService(connectors.FileSystem, Models, Status, Store, LoadTracker, TimeSpan.FromSeconds(options.LoadGraceSeconds));
        Dependencies = new DependencyLaunchService(options, connectors.Payloads, connectors.Launcher, connectors.Elfldr, Status, Store);
        Closer = new AppCloseService(connectors.FileSystem, Status, Store, LoadTracker, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(1));
        Apps = new AppLaunchService(connectors.AppControl ?? [], Status, Store);
    }

    /// <summary>The stable slug used in URLs, persisted transfers and logs.</summary>
    public string Id { get; }

    /// <summary>Whether this console came from configuration or was added in the companion.</summary>
    public ConsoleSource Source { get; }

    /// <summary>The text shown to users.</summary>
    public string Name { get; }

    public string Host => Options.Host;

    public PS5Options Options { get; }

    public IPS5FileSystem FileSystem { get; }

    public CompanionStatusStore Store { get; }

    public LoadTracker LoadTracker { get; }

    public CompanionStatusService Status { get; }

    public ModelCatalog Models { get; }

    public ModelControlService Control { get; }

    public DependencyLaunchService Dependencies { get; }

    public AppLaunchService Apps { get; }

    public AppCloseService Closer { get; }

    public void Dispose() => (FileSystem as IDisposable)?.Dispose();
}

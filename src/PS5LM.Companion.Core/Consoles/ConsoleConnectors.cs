using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>Everything that talks to one console over the network. Built by Infrastructure, faked in tests.</summary>
public sealed record ConsoleConnectors(
    IPS5FileSystem FileSystem,
    ILlamaServerProbe Llama,
    IConsoleSensorProbe Sensors,
    IPayloadCatalog Payloads,
    IPayloadLauncher Launcher,
    IElfldrProbe Elfldr,
    IReadOnlyList<IAppControlProvider>? AppControl = null);

/// <summary>Creates the network clients for one configured console.</summary>
public interface IConsoleConnectorFactory
{
    ConsoleConnectors Create(string consoleId, PS5Options options);
}

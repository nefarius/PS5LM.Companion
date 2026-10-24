using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Tests.Support;

/// <summary>The in-memory connectors of one fake console.</summary>
internal sealed class FakeConsole(FakeFileSystem? fileSystem = null)
{
    public FakeFileSystem FileSystem { get; } = fileSystem ?? ConsoleFixtures.PS5LMInstalled();

    public FakeLlamaProbe Llama { get; } = new();

    public FakeSensorProbe Sensors { get; } = new();

    public FakePayloadCatalog Payloads { get; } = new();

    public FakePayloadLauncher Launcher { get; } = new();

    public FakeElfldrProbe Elfldr { get; } = new();

    public ConsoleConnectors ToConnectors() => new(FileSystem, Llama, Sensors, Payloads, Launcher, Elfldr);
}

/// <summary>Hands out fake connectors per console id, creating them on first use.</summary>
internal sealed class FakeConnectorFactory : IConsoleConnectorFactory
{
    private readonly Dictionary<string, FakeConsole> _consoles = new(StringComparer.OrdinalIgnoreCase);

    public FakeConsole this[string id]
    {
        get
        {
            lock (_consoles)
            {
                if (!_consoles.TryGetValue(id, out var console))
                {
                    _consoles[id] = console = new FakeConsole();
                }

                return console;
            }
        }
    }

    public FakeConsole Replace(string id, FakeConsole console)
    {
        lock (_consoles)
        {
            return _consoles[id] = console;
        }
    }

    public ConsoleConnectors Create(string consoleId, PS5Options options) => this[consoleId].ToConnectors();
}

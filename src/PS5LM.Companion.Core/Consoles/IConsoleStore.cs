using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>One user-added console as it is stored, separate from configuration.</summary>
public sealed record StoredConsole(string Id, PS5Options Options);

/// <summary>The user-added consoles. Configuration consoles are not stored here.</summary>
public interface IConsoleStore
{
    IReadOnlyList<StoredConsole> Load();

    void Save(IReadOnlyList<StoredConsole> consoles);
}

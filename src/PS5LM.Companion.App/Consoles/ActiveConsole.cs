using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.App.Consoles;

/// <summary>
/// The console one browser circuit is looking at. Scoped, so two tabs can show two consoles. It only chooses
/// what the UI shows: queued transfers and running commands always belong to the console they were started for.
/// </summary>
public sealed class ActiveConsole(IConsoleRegistry registry)
{
    public ConsoleRuntime Runtime { get; private set; } = registry.Default;

    public IReadOnlyList<ConsoleRuntime> All => registry.All;

    public event Action? Changed;

    /// <summary>Selects a console by id; unknown ids and the current selection are ignored.</summary>
    public bool Select(string? id)
    {
        var next = registry.Find(id);
        if (next is null || ReferenceEquals(next, Runtime))
        {
            return false;
        }

        Runtime = next;
        Changed?.Invoke();
        return true;
    }
}

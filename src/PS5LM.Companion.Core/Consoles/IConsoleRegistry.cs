namespace PS5LM.Companion.Core.Consoles;

/// <summary>The configured consoles. The list is fixed at startup.</summary>
public interface IConsoleRegistry
{
    IReadOnlyList<ConsoleRuntime> All { get; }

    /// <summary>The console selected when nothing else is chosen.</summary>
    ConsoleRuntime Default { get; }

    /// <summary>Looks a console up by id, ignoring case; <see langword="null"/> when it is not configured.</summary>
    ConsoleRuntime? Find(string? id);
}

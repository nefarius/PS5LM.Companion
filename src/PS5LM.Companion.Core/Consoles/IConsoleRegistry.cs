using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>Where a console's settings came from.</summary>
public enum ConsoleSource
{
    /// <summary>Bound from configuration. The companion will not change it.</summary>
    Configuration = 0,

    /// <summary>Added in the companion and stored in the data directory.</summary>
    User = 1,
}

public enum ConsoleChangeKind
{
    Added = 0,
    Removed = 1,
    Replaced = 2,
}

/// <summary>A console was added, removed, or replaced by a new runtime with the same id.</summary>
public sealed record ConsoleChange(ConsoleChangeKind Kind, ConsoleRuntime? Previous, ConsoleRuntime? Current);

public enum ConsoleEditStatus
{
    Ok = 0,
    Invalid = 1,
    Conflict = 2,
    ReadOnly = 3,
    NotFound = 4,
}

public sealed record ConsoleEditResult(ConsoleEditStatus Status, string? Message, ConsoleRuntime? Console = null)
{
    public bool Succeeded => Status == ConsoleEditStatus.Ok;
}

/// <summary>The consoles this companion manages. The list can change after startup.</summary>
public interface IConsoleRegistry
{
    IReadOnlyList<ConsoleRuntime> All { get; }

    /// <summary>Raised after a console is added, removed or replaced. Not raised for a rejected edit.</summary>
    event Action<ConsoleChange>? Changed;

    /// <summary>Looks a console up by id, ignoring case; <see langword="null"/> when it is not configured.</summary>
    ConsoleRuntime? Find(string? id);

    /// <summary>Adds a user console. The id is stored in lower case.</summary>
    ConsoleEditResult Add(string id, PS5Options options);

    /// <summary>Replaces a user console's settings. The id does not change.</summary>
    ConsoleEditResult Update(string id, PS5Options options);

    /// <summary>Removes a user console.</summary>
    ConsoleEditResult Remove(string id);
}

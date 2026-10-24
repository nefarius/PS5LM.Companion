using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Core.Consoles;

/// <summary>
/// The consoles this companion manages. Bound from the <c>PS5</c> configuration section:
/// <c>PS5:Consoles:{id}:Host</c> and the other <see cref="PS5Options"/> keys, plus an optional <c>PS5:DefaultConsoleId</c>.
/// </summary>
public sealed class ConsolesOptions
{
    public const string SectionName = "PS5";

    /// <summary>Console settings keyed by a stable slug (lowercase letters, digits and hyphens).</summary>
    public Dictionary<string, PS5Options> Consoles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The console the dashboard selects first. Empty picks the first id in alphabetical order.</summary>
    public string? DefaultConsoleId { get; set; }
}

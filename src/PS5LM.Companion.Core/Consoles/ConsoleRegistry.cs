namespace PS5LM.Companion.Core.Consoles;

/// <summary>Builds one <see cref="ConsoleRuntime"/> per configured console and owns their lifetime.</summary>
public sealed class ConsoleRegistry : IConsoleRegistry, IDisposable
{
    private readonly Dictionary<string, ConsoleRuntime> _byId;

    public ConsoleRegistry(ConsolesOptions options, IConsoleConnectorFactory factory, TimeProvider time)
    {
        var runtimes = new List<ConsoleRuntime>();
        foreach (var (key, consoleOptions) in options.Consoles.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var id = key.ToLowerInvariant();
            runtimes.Add(new ConsoleRuntime(id, consoleOptions, factory.Create(id, consoleOptions), time));
        }

        if (runtimes.Count == 0)
        {
            throw new InvalidOperationException("At least one console must be configured under PS5:Consoles.");
        }

        All = runtimes;
        _byId = runtimes.ToDictionary(r => r.Id, StringComparer.Ordinal);
        Default = string.IsNullOrWhiteSpace(options.DefaultConsoleId)
            ? runtimes[0]
            : Find(options.DefaultConsoleId) ?? throw new InvalidOperationException($"PS5:DefaultConsoleId '{options.DefaultConsoleId}' is not a configured console.");
    }

    public IReadOnlyList<ConsoleRuntime> All { get; }

    public ConsoleRuntime Default { get; }

    public ConsoleRuntime? Find(string? id) =>
        id is not null && _byId.TryGetValue(id.ToLowerInvariant(), out var runtime) ? runtime : null;

    public void Dispose()
    {
        foreach (var runtime in All)
        {
            runtime.Dispose();
        }
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Infrastructure.Persistence;

/// <summary>User-added consoles in a JSON file, replaced atomically so a crash never truncates it.</summary>
public sealed partial class JsonConsoleStore(string path, ILogger<JsonConsoleStore> logger) : IConsoleStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public IReadOnlyList<StoredConsole> Load()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        List<Entry>? entries;
        lock (_gate)
        {
            try
            {
                entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Json);
            }
            catch (JsonException ex)
            {
                LogUnreadable(ex, path);
                TryPreserveUnreadable();
                return [];
            }
        }

        var consoles = new List<StoredConsole>();
        foreach (var entry in entries ?? [])
        {
            var id = entry.Id?.Trim().ToLowerInvariant() ?? "";
            if (entry.Options is null)
            {
                LogSkipped(id.Length == 0 ? "(missing id)" : id, "it has no settings");
                continue;
            }

            var errors = ConsoleSettings.Errors(id, entry.Options);
            if (errors.Count > 0)
            {
                LogSkipped(id.Length == 0 ? "(missing id)" : id, string.Join(" ", errors));
                continue;
            }

            consoles.Add(new StoredConsole(id, entry.Options));
        }

        return consoles;
    }

    public void Save(IReadOnlyList<StoredConsole> consoles)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(consoles, Json));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>Moves a corrupt file aside. A failure here still leaves <see cref="Load"/> with an empty list.</summary>
    private void TryPreserveUnreadable()
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            File.Move(path, $"{path}.{stamp}.unreadable");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogPreserveFailed(ex, path);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read {Path}; no stored consoles were loaded")]
    private partial void LogUnreadable(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not move the unreadable console file {Path} aside")]
    private partial void LogPreserveFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored console {ConsoleId} was skipped: {Reason}")]
    private partial void LogSkipped(string consoleId, string reason);

    private sealed record Entry(string? Id, PS5Options? Options);
}

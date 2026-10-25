using System.Text.Json;

using Microsoft.Extensions.Logging;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Inference;

namespace PS5LM.Companion.Infrastructure.Persistence;

/// <summary>User-added inference pools in a JSON file, replaced atomically.</summary>
public sealed partial class JsonInferencePoolStore(string path, ILogger<JsonInferencePoolStore> logger, IConsoleRegistry consoles) : IInferencePoolStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly Lock _gate = new();

    public IReadOnlyList<StoredInferencePool> Load()
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
                return [];
            }
        }

        var pools = new List<StoredInferencePool>();
        foreach (var entry in entries ?? [])
        {
            var alias = entry.Alias?.Trim().ToLowerInvariant() ?? "";
            var consoleIds = entry.Consoles ?? [];
            var errors = InferencePoolRules.Errors(alias, entry.Model, consoleIds, id => consoles.Find(id) is not null);
            if (errors.Count > 0)
            {
                LogSkipped(alias.Length == 0 ? "(missing alias)" : alias, string.Join(" ", errors));
                continue;
            }

            pools.Add(new StoredInferencePool(alias, entry.Model!.Trim(), consoleIds.Select(id => id.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList()));
        }

        return pools;
    }

    public void Save(IReadOnlyList<StoredInferencePool> pools)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(pools, Json));
            File.Move(temp, path, overwrite: true);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read {Path}; no stored inference pools were loaded")]
    private partial void LogUnreadable(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stored inference pool {Alias} was skipped: {Reason}")]
    private partial void LogSkipped(string alias, string reason);

    private sealed record Entry(string? Alias, string? Model, List<string>? Consoles);
}

using System.Text.Json;
using System.Text.Json.Serialization;

using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Infrastructure.Persistence;

/// <summary>Keeps the transfer list in a JSON file, replaced atomically so a crash never truncates it.</summary>
public sealed class JsonTransferStore(TransferOptions options) : ITransferStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<Transfer>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.StateFile))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(options.StateFile);
            return await JsonSerializer.DeserializeAsync<List<Transfer>>(stream, Json, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt state file must not stop the companion; the transfers are re-requestable.
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IReadOnlyList<Transfer> transfers, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(options.StateFile))!;
            Directory.CreateDirectory(directory);
            var temp = options.StateFile + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, transfers, Json, cancellationToken);
            }

            File.Move(temp, options.StateFile, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Infrastructure.Persistence;

/// <summary>Keeps transfers and console copies in a JSON file, replaced atomically so a crash never truncates it.</summary>
public sealed class JsonTransferStore(TransferOptions options) : ITransferStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int? _refusedVersion;

    public async Task<TransferStoreState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.StateFile))
        {
            return TransferStoreState.Empty;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var stream = File.OpenRead(options.StateFile);
            var first = FirstByte(stream);
            stream.Position = 0;
            if (first < 0)
            {
                return TransferStoreState.Empty;
            }

            if (first == '[')
            {
                var transfers = await JsonSerializer.DeserializeAsync<List<Transfer>>(stream, Json, cancellationToken) ?? [];
                return new TransferStoreState(1, transfers, [], []);
            }

            var state = await JsonSerializer.DeserializeAsync<TransferStoreState>(stream, Json, cancellationToken)
                        ?? TransferStoreState.Empty;
            if (state.Version > TransferStoreState.CurrentVersion)
            {
                _refusedVersion = state.Version;
                throw new InvalidDataException(
                    $"Transfer state version {state.Version} is newer than this companion supports ({TransferStoreState.CurrentVersion}).");
            }

            return new TransferStoreState(
                state.Version,
                state.Transfers ?? [],
                state.CopyBatches ?? [],
                state.CopyItems ?? []);
        }
        catch (JsonException)
        {
            // A corrupt state file must not stop the companion; the transfers are re-requestable.
            return TransferStoreState.Empty;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(TransferStoreState state, CancellationToken cancellationToken)
    {
        if (_refusedVersion is int version)
        {
            throw new InvalidDataException(
                $"Transfer state version {version} was left untouched because this companion cannot read it.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(options.StateFile))!;
            Directory.CreateDirectory(directory);
            var temp = options.StateFile + ".tmp";
            var envelope = state with { Version = TransferStoreState.CurrentVersion };
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, Json, cancellationToken);
            }

            File.Move(temp, options.StateFile, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int FirstByte(Stream stream)
    {
        int value;
        do
        {
            value = stream.ReadByte();
        }
        while (value is ' ' or '\n' or '\r' or '\t');

        return value;
    }
}

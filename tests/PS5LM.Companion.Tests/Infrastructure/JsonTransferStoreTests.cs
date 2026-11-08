using PS5LM.Companion.Core.Transfers;
using PS5LM.Companion.Infrastructure.Persistence;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class JsonTransferStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));
    private readonly JsonTransferStore _store;

    public JsonTransferStoreTests() =>
        _store = new JsonTransferStore(new TransferOptions { StateFile = Path.Combine(_dir, "nested", "transfers.json") });

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Transfer Sample() => new(
        Guid.NewGuid(), "ps5", "a/b", "m.gguf", "abc", 10, "Internal storage", TransferState.Uploading, 10, 4, null,
        new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Round_trips_transfers_and_creates_the_folder()
    {
        var transfer = Sample();

        await _store.SaveAsync(new TransferStoreState(TransferStoreState.CurrentVersion, [transfer], [], []), CancellationToken.None);

        Assert.Equal([transfer], (await _store.LoadAsync(CancellationToken.None)).Transfers);
        Assert.Contains("\"Uploading\"", await File.ReadAllTextAsync(Path.Combine(_dir, "nested", "transfers.json")));
    }

    [Fact]
    public async Task A_missing_file_means_no_transfers() =>
        Assert.Empty((await _store.LoadAsync(CancellationToken.None)).Transfers);

    [Fact]
    public async Task A_corrupt_file_is_treated_as_empty()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "nested", "transfers.json"), "{ not json");

        Assert.Empty((await _store.LoadAsync(CancellationToken.None)).Transfers);
    }

    [Fact]
    public async Task Saving_replaces_the_previous_content_without_leaving_a_temp_file()
    {
        await _store.SaveAsync(new TransferStoreState(TransferStoreState.CurrentVersion, [Sample(), Sample()], [], []), CancellationToken.None);
        await _store.SaveAsync(new TransferStoreState(TransferStoreState.CurrentVersion, [Sample()], [], []), CancellationToken.None);

        Assert.Single((await _store.LoadAsync(CancellationToken.None)).Transfers);
        Assert.Equal(["transfers.json"], Directory.GetFiles(Path.Combine(_dir, "nested")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_legacy_array_is_read_as_version_one()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        var transfer = Sample();
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "nested", "transfers.json"),
            System.Text.Json.JsonSerializer.Serialize(new[] { transfer }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            }));

        var state = await _store.LoadAsync(CancellationToken.None);

        Assert.Equal(1, state.Version);
        Assert.Equal(transfer.Id, Assert.Single(state.Transfers).Id);
        Assert.Empty(state.CopyBatches);
    }

    [Fact]
    public async Task A_newer_state_version_is_refused_and_left_in_place()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        var path = Path.Combine(_dir, "nested", "transfers.json");
        await File.WriteAllTextAsync(path, """{ "version": 99, "transfers": [], "copyBatches": [], "copyItems": [] }""");

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.LoadAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.SaveAsync(TransferStoreState.Empty, CancellationToken.None));
        Assert.Contains("\"version\": 99", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_batches_round_trip_with_their_items()
    {
        var now = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
        var batch = new CopyBatch(Guid.NewGuid(), "ps5", [Guid.NewGuid()], CopyBatchState.Queued, now, now);
        var item = new CopyItem(
            batch.ItemIds[0], batch.Id, "ps5", "/data/PS5LM/models/a.gguf", "/data/PS5LM/models/a.gguf", "a.gguf",
            "Internal storage", 4, null, CopyItemState.Queued, 0, null,
            [new CopyTarget("den", "Internal storage", CopyTargetOutcome.Queued, Guid.NewGuid(), null)], now, now);

        await _store.SaveAsync(new TransferStoreState(1, [Sample()], [batch], [item]), CancellationToken.None);
        var state = await _store.LoadAsync(CancellationToken.None);

        Assert.Equal(TransferStoreState.CurrentVersion, state.Version);
        Assert.Equal(batch.Id, Assert.Single(state.CopyBatches).Id);
        Assert.Equal(item.Id, Assert.Single(state.CopyItems).Id);
        Assert.Contains("\"version\": 2", await File.ReadAllTextAsync(Path.Combine(_dir, "nested", "transfers.json")), StringComparison.Ordinal);
    }
}

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
        Guid.NewGuid(), "a/b", "m.gguf", "abc", 10, "Internal storage", TransferState.Uploading, 10, 4, null,
        new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Round_trips_transfers_and_creates_the_folder()
    {
        var transfer = Sample();

        await _store.SaveAsync([transfer], CancellationToken.None);

        Assert.Equal([transfer], await _store.LoadAsync(CancellationToken.None));
        Assert.Contains("\"Uploading\"", await File.ReadAllTextAsync(Path.Combine(_dir, "nested", "transfers.json")));
    }

    [Fact]
    public async Task A_missing_file_means_no_transfers() => Assert.Empty(await _store.LoadAsync(CancellationToken.None));

    [Fact]
    public async Task A_corrupt_file_is_treated_as_empty()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "nested", "transfers.json"), "{ not json");

        Assert.Empty(await _store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Saving_replaces_the_previous_content_without_leaving_a_temp_file()
    {
        await _store.SaveAsync([Sample(), Sample()], CancellationToken.None);
        await _store.SaveAsync([Sample()], CancellationToken.None);

        Assert.Single(await _store.LoadAsync(CancellationToken.None));
        Assert.Equal(["transfers.json"], Directory.GetFiles(Path.Combine(_dir, "nested")).Select(Path.GetFileName));
    }
}

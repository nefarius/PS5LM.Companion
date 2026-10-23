using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Tests.Support;

/// <summary>A transfer service wired to in-memory fakes and a throwaway cache directory.</summary>
internal sealed class TransferHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    public TransferHarness(FakeFileSystem? fileSystem = null)
    {
        FileSystem = fileSystem ?? ConsoleFixtures.PS5LMInstalled();
        CacheDirectory = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        Options = new TransferOptions
        {
            CacheDirectory = CacheDirectory,
            StateFile = Path.Combine(CacheDirectory, "transfers.json"),
            SpaceMarginBytes = 100,
        };
        Status = new CompanionStatusService(new PS5Options { Host = "ps5" }, FileSystem, Llama, Sensors, new LoadTracker(time), time);
        Store = new CompanionStatusStore();
        Service = new TransferService(Hub, FileSystem, new ModelCatalog(FileSystem), Status, Store, TransferStore, Options, time);
    }

    public FakeFileSystem FileSystem { get; }

    public FakeHub Hub { get; } = new();

    public FakeLlamaProbe Llama { get; } = new();

    public FakeSensorProbe Sensors { get; } = new();

    public MemoryTransferStore TransferStore { get; } = new();

    public TransferOptions Options { get; }

    public string CacheDirectory { get; }

    public CompanionStatusService Status { get; }

    public CompanionStatusStore Store { get; }

    public TransferService Service { get; }

    /// <summary>Detects the console and publishes the status, as the status monitor does.</summary>
    public async Task PrimeAsync() => Store.Publish(await Status.RefreshAsync(CancellationToken.None));

    public void StartWorker() => _worker = Service.RunAsync(_stop.Token);

    public async Task<Transfer> WaitUntilFinishedAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var transfer = Service.Snapshot().Single(t => t.Id == id);
            if (!transfer.IsActive)
            {
                return transfer;
            }

            await Task.Delay(10, timeout.Token);
        }
    }

    public string CachedFile(string repo, string name) => Path.Combine(CacheDirectory, repo.Replace('/', '_'), name);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_worker is not null)
        {
            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _stop.Dispose();
        if (Directory.Exists(CacheDirectory))
        {
            Directory.Delete(CacheDirectory, recursive: true);
        }
    }
}

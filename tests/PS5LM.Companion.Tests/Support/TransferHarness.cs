using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Tests.Support;

/// <summary>A transfer service wired to in-memory fakes and a throwaway cache directory.</summary>
internal sealed class TransferHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    public TransferHarness(FakeFileSystem? fileSystem = null, params string[] extraConsoles)
    {
        Connectors = new FakeConnectorFactory();
        Primary = fileSystem is null ? Connectors[PrimaryId] : Connectors.Replace(PrimaryId, new FakeConsole(fileSystem));
        CacheDirectory = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
        Options = new TransferOptions
        {
            CacheDirectory = CacheDirectory,
            StateFile = Path.Combine(CacheDirectory, "transfers.json"),
            SpaceMarginBytes = 100,
        };
        var consoles = new ConsolesOptions { DefaultConsoleId = PrimaryId };
        foreach (var id in extraConsoles.Prepend(PrimaryId))
        {
            consoles.Consoles[id] = new PS5Options { Host = id };
        }

        Registry = new ConsoleRegistry(consoles, Connectors, Time);
        Service = new TransferService(Hub, Registry, TransferStore, Options, Time);
    }

    public const string PrimaryId = "ps5";

    public FakeConnectorFactory Connectors { get; }

    public FakeConsole Primary { get; }

    public FakeTimeProvider Time { get; }

    public IConsoleRegistry Registry { get; }

    public ConsoleRuntime Runtime => Registry.Default;

    public FakeFileSystem FileSystem => Primary.FileSystem;

    public FakeHub Hub { get; } = new();

    public FakeLlamaProbe Llama => Primary.Llama;

    public FakeSensorProbe Sensors => Primary.Sensors;

    public MemoryTransferStore TransferStore { get; } = new();

    public TransferOptions Options { get; }

    public string CacheDirectory { get; }

    public CompanionStatusService Status => Runtime.Status;

    public CompanionStatusStore Store => Runtime.Store;

    public TransferService Service { get; }

    public TransferRequest Request(string repo, string file, string? targetRoot = null, string? consoleId = null) =>
        new(consoleId ?? PrimaryId, repo, file, targetRoot);

    /// <summary>Detects the console and publishes the status, as the status monitor does.</summary>
    public async Task PrimeAsync()
    {
        foreach (var console in Registry.All)
        {
            console.Store.Publish(await console.Status.RefreshAsync(CancellationToken.None));
        }
    }

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

    public string CachedFile(string repo, string name, string consoleId = PrimaryId) => Path.Combine(CacheDirectory, consoleId, repo, name);

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

        (Registry as IDisposable)?.Dispose();
        _stop.Dispose();
        if (Directory.Exists(CacheDirectory))
        {
            Directory.Delete(CacheDirectory, recursive: true);
        }
    }
}

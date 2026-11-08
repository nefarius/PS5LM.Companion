using System.Security.Cryptography;

using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Tests.Support;

internal sealed class FakeHub : IModelHub
{
    public static byte[] Content { get; } = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

    public static string Sha256 => Convert.ToHexString(SHA256.HashData(Content));

    public List<HubRepo> Repos { get; } = [new("acme/tiny-GGUF", 1234)];

    public Dictionary<string, List<HubFile>> Files { get; } = new()
    {
        ["acme/tiny-GGUF"] = [new HubFile("tiny-Q4.gguf", Content.Length, Sha256)],
    };

    /// <summary>Throws from search and listing, like an unreachable hub.</summary>
    public bool Unavailable { get; set; }

    /// <summary>Fails the download after this many bytes were written, once.</summary>
    public int? FailAfterBytes { get; set; }

    /// <summary>When set, the download waits for this task before writing.</summary>
    public TaskCompletionSource<bool>? DownloadGate { get; set; }

    public byte[]? ServedContent { get; set; }

    public List<long> ResumeOffsets { get; } = [];

    public Task<IReadOnlyList<HubRepo>> SearchAsync(string query, CancellationToken cancellationToken) =>
        Unavailable ? throw new HubUnavailableException("hub down") : Task.FromResult<IReadOnlyList<HubRepo>>(Repos);

    public Task<IReadOnlyList<HubFile>> ListFilesAsync(string repo, CancellationToken cancellationToken) =>
        Unavailable
            ? throw new HubUnavailableException("hub down")
            : Task.FromResult<IReadOnlyList<HubFile>>(Files.TryGetValue(repo, out var files) ? files : []);

    public Task<Stream> OpenHeaderAsync(string repo, string fileName, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new MemoryStream(ServedContent ?? Content, writable: false));

    public async Task DownloadAsync(string repo, string fileName, string destinationPath, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        var content = ServedContent ?? Content;
        var existing = File.Exists(destinationPath) ? new FileInfo(destinationPath).Length : 0;
        ResumeOffsets.Add(existing);

        await using var target = new FileStream(destinationPath, FileMode.Append, FileAccess.Write);
        var written = existing;
        while (written < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(1000, content.Length - written);
            await target.WriteAsync(content.AsMemory((int)written, chunk), cancellationToken);
            written += chunk;
            progress?.Report(written);

            if (FailAfterBytes is { } limit && written >= limit)
            {
                FailAfterBytes = null;
                throw new IOException("The connection to the hub dropped.");
            }
        }
    }
}

internal sealed class MemoryTransferStore : ITransferStore
{
    public List<Transfer> Saved { get; set; } = [];

    public List<CopyBatch> Batches { get; set; } = [];

    public List<CopyItem> Items { get; set; } = [];

    public Task<TransferStoreState> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new TransferStoreState(TransferStoreState.CurrentVersion, Saved.ToList(), Batches.ToList(), Items.ToList()));

    /// <summary>Simulates a full disk.</summary>
    public bool FailSaves { get; set; }

    public Task SaveAsync(TransferStoreState state, CancellationToken cancellationToken)
    {
        if (FailSaves)
        {
            throw new IOException("No space left on device.");
        }

        Saved = state.Transfers.ToList();
        Batches = state.CopyBatches.ToList();
        Items = state.CopyItems.ToList();
        return Task.CompletedTask;
    }
}

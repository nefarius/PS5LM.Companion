namespace PS5LM.Companion.Core.Transfers;

public enum TransferState
{
    Queued = 0,
    Downloading = 1,
    Verifying = 2,
    Uploading = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

/// <summary>A model file on a hub repository.</summary>
/// <param name="Sha256">The LFS object id, which is the file's SHA-256, when the hub provides it.</param>
public sealed record HubFile(string Name, long SizeBytes, string? Sha256)
{
    /// <summary>
    /// False for vision projectors (<c>mmproj-*</c>) and importance matrices (<c>imatrix*</c>): they sit beside
    /// the models in a repository but cannot be loaded on their own.
    /// </summary>
    public bool IsLoadableModel =>
        !Name.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
        && !Name.StartsWith("imatrix", StringComparison.OrdinalIgnoreCase);
}

public sealed record HubRepo(string Id, long Downloads);

public sealed record TransferRequest(string ConsoleId, string Repo, string FileName, string? TargetRoot);

/// <summary>One model transfer: hub → companion cache → the console named by <c>ConsoleId</c>.</summary>
public sealed record Transfer(
    Guid Id,
    string ConsoleId,
    string Repo,
    string FileName,
    string? Sha256,
    long SizeBytes,
    string TargetRoot,
    TransferState State,
    long DownloadedBytes,
    long UploadedBytes,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsActive => State is TransferState.Queued or TransferState.Downloading or TransferState.Verifying or TransferState.Uploading;
}

/// <summary>Thrown when the model hub cannot be reached or refuses a search or listing.</summary>
public sealed class HubUnavailableException : Exception
{
    public HubUnavailableException()
    {
    }

    public HubUnavailableException(string message)
        : base(message)
    {
    }

    public HubUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The model hub (Hugging Face): search, list files, download with resume.</summary>
public interface IModelHub
{
    Task<IReadOnlyList<HubRepo>> SearchAsync(string query, CancellationToken cancellationToken);

    Task<IReadOnlyList<HubFile>> ListFilesAsync(string repo, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads to <paramref name="destinationPath"/>, resuming a partial file already there, and reports the
    /// total bytes present. Throws when the hub refuses or the connection drops; a retry resumes.
    /// </summary>
    Task DownloadAsync(string repo, string fileName, string destinationPath, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>Persists the transfer list so downloads survive a restart of the companion.</summary>
public interface ITransferStore
{
    Task<IReadOnlyList<Transfer>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(IReadOnlyList<Transfer> transfers, CancellationToken cancellationToken);
}

/// <summary>Where the companion keeps downloaded models until they are on the console.</summary>
public sealed class TransferOptions
{
    public string CacheDirectory { get; set; } = Path.Combine("data", "cache");

    /// <summary>The JSON file holding the transfer list.</summary>
    public string StateFile { get; set; } = Path.Combine("data", "transfers.json");

    /// <summary>Space to leave free on the console drive after an upload.</summary>
    public long SpaceMarginBytes { get; set; } = 1L * 1024 * 1024 * 1024;

    /// <summary>How many finished transfers are remembered.</summary>
    public int HistoryLimit { get; set; } = 50;
}

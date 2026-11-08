using PS5LM.Companion.Core.Models;

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

    /// <summary>A console-to-console copy is reading the source file before the destination upload starts.</summary>
    Staging = 7,
}

/// <summary>Where a transfer's bytes come from.</summary>
public enum TransferSourceKind
{
    /// <summary>Downloaded from the model hub into the companion cache.</summary>
    Hub = 0,

    /// <summary>Staged from a console, then uploaded to one or more other consoles.</summary>
    Console = 1,
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

/// <param name="Parameters">Parameter count from the hub's GGUF metadata, when the search returned it.</param>
/// <param name="Architecture">GGUF architecture from that metadata, when the search returned it.</param>
public sealed record HubRepo(string Id, long Downloads, long? Parameters = null, string? Architecture = null);

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
    DateTimeOffset UpdatedAt,
    TransferSourceKind SourceKind = TransferSourceKind.Hub,
    Guid? CopyBatchId = null,
    Guid? CopyItemId = null,
    string? SourceConsoleId = null,
    string? SourceAppPath = null)
{
    public bool IsActive => State is TransferState.Queued or TransferState.Downloading or TransferState.Verifying
        or TransferState.Uploading or TransferState.Staging;
}

public enum CopyBatchState
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    CompletedWithErrors = 3,
    Cancelled = 4,
}

public enum CopyItemState
{
    Queued = 0,
    Staging = 1,
    Verifying = 2,
    Ready = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6,
}

public enum CopyTargetOutcome
{
    Queued = 0,
    AlreadyPresent = 1,
    AlreadyQueued = 2,
    Unavailable = 3,
    InvalidTarget = 4,
    Failed = 5,
    Completed = 6,
    Cancelled = 7,
}

/// <summary>One user request to copy one or more models from a console.</summary>
public sealed record CopyBatch(
    Guid Id,
    string SourceConsoleId,
    IReadOnlyList<Guid> ItemIds,
    CopyBatchState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsActive => State is CopyBatchState.Queued or CopyBatchState.Running;
}

/// <summary>One physical source file, staged once and uploaded to each queued destination.</summary>
public sealed record CopyItem(
    Guid Id,
    Guid BatchId,
    string SourceConsoleId,
    string SourceFtpPath,
    string SourceAppPath,
    string FileName,
    string SourceLocation,
    long SizeBytes,
    string? Sha256,
    CopyItemState State,
    long StagedBytes,
    string? Error,
    IReadOnlyList<CopyTarget> Targets,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsActive => State is CopyItemState.Queued or CopyItemState.Staging or CopyItemState.Verifying or CopyItemState.Ready;
}

/// <summary>What a copy item should do on one destination console and storage root.</summary>
public sealed record CopyTarget(
    string ConsoleId,
    string TargetRoot,
    CopyTargetOutcome Outcome,
    Guid? TransferId,
    string? Message);

/// <summary>A destination of a console-to-console copy. A null root uses that console's first upload location.</summary>
public sealed record CopyTargetRequest(string ConsoleId, string? TargetRoot);

/// <summary>Copies the models at <paramref name="Paths"/> from one console onto each destination.</summary>
public sealed record CopyRequest(string SourceConsoleId, IReadOnlyList<string> Paths, IReadOnlyList<CopyTargetRequest> Targets);

/// <summary>The durable result of asking for a copy. <see cref="Batch"/> is null when the request was rejected.</summary>
public sealed record CopyEnqueueResult(OperationOutcome Outcome, string? Message, CopyBatch? Batch, IReadOnlyList<CopyItem> Items);

/// <summary>A copy batch with the items and destination transfers it owns.</summary>
public sealed record CopyBatchView(CopyBatch Batch, IReadOnlyList<CopyItem> Items, IReadOnlyList<Transfer> Transfers);

/// <summary>Version 2 of the transfer file. Version 1 was a JSON array of hub transfers.</summary>
public sealed record TransferStoreState(
    int Version,
    IReadOnlyList<Transfer> Transfers,
    IReadOnlyList<CopyBatch> CopyBatches,
    IReadOnlyList<CopyItem> CopyItems)
{
    public const int CurrentVersion = 2;

    public static TransferStoreState Empty { get; } = new(CurrentVersion, [], [], []);
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
    /// The first bytes of a repository file, at most 16 MiB, enough for the GGUF metadata.
    /// The caller disposes the stream, which stops the download. Throws <see cref="HubUnavailableException"/>
    /// when the hub refuses or does not answer.
    /// </summary>
    Task<Stream> OpenHeaderAsync(string repo, string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads to <paramref name="destinationPath"/>, resuming a partial file already there, and reports the
    /// total bytes present. Throws when the hub refuses or the connection drops; a retry resumes.
    /// </summary>
    Task DownloadAsync(string repo, string fileName, string destinationPath, IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>Persists transfers and console-to-console copies so they survive a restart of the companion.</summary>
public interface ITransferStore
{
    Task<TransferStoreState> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(TransferStoreState state, CancellationToken cancellationToken);
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

    /// <summary>How many finished console-to-console copy batches are remembered. A batch keeps its items and destination transfers.</summary>
    public int CopyHistoryLimit { get; set; } = 20;
}

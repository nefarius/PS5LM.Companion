using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;

using FluentFTP;
using FluentFTP.Exceptions;

using Microsoft.Extensions.Logging;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Infrastructure.Ftp;

/// <summary>
/// <see cref="IPS5FileSystem"/> over FluentFTP. ftpsrv is a small server, so every operation uses a short-lived
/// connection: that avoids a stale control connection after the console sleeps. A console has two lanes, so a long
/// upload does not hold up listings and small reads and writes. Callers never write one path from both lanes at once.
/// </summary>
public sealed class FtpFileSystem(PS5Options options, ILogger<FtpFileSystem> logger) : IPS5FileSystem, IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Waiting this long behind another operation is worth a log line.</summary>
    private static readonly TimeSpan SlowGateWait = TimeSpan.FromSeconds(1);

    private readonly LaneSlot _control = new();
    private readonly LaneSlot _bulk = new();

    public void Dispose()
    {
        _control.Gate.Dispose();
        _bulk.Gate.Dispose();
    }

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(async client => path == "/" || await FindAsync(client, path, cancellationToken) is { Type: FtpObjectType.Directory }, cancellationToken);

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(async client => await FindAsync(client, path, cancellationToken) is { Type: FtpObjectType.File }, cancellationToken);

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken) =>
        await TryListAsync(path, cancellationToken) ?? [];

    public Task<IReadOnlyList<RemoteEntry>?> TryListAsync(string path, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<RemoteEntry>?>(
            async client =>
            {
                var items = await TryListAsync(client, path, cancellationToken);
                return items?
                    .Where(i => i.Name is not "." and not ".." && i.Type is FtpObjectType.File or FtpObjectType.Directory)
                    .Select(i => new RemoteEntry(
                        i.Name,
                        i.FullName,
                        i.Type == FtpObjectType.Directory,
                        i.Size < 0 ? 0 : i.Size,
                        i.Modified == default ? null : new DateTimeOffset(i.Modified, TimeSpan.Zero)))
                    .ToList();
            },
            cancellationToken);

    public Task<string?> ReadTextAsync(string path, int maxBytes, CancellationToken cancellationToken) =>
        RunAsync<string?>(
            async client =>
            {
                if (await FindAsync(client, path, cancellationToken) is not { Type: FtpObjectType.File } file)
                {
                    return null;
                }

                if (file.Size > maxBytes)
                {
                    return null;
                }

                using var stream = new MemoryStream();
                return await ReadIntoAsync(client, stream, path, cancellationToken)
                    ? Encoding.UTF8.GetString(stream.ToArray())
                    : null;
            },
            cancellationToken);

    public Task<byte[]?> ReadBytesAsync(string path, int maxBytes, CancellationToken cancellationToken) =>
        RunAsync<byte[]?>(
            async client =>
            {
                if (await FindAsync(client, path, cancellationToken) is not { Type: FtpObjectType.File } file || file.Size > maxBytes)
                {
                    return null;
                }

                using var stream = new MemoryStream();
                return await ReadIntoAsync(client, stream, path, cancellationToken) ? stream.ToArray() : null;
            },
            cancellationToken);

    // FluentFTP reports a refused RETR (ftpsrv answers 550 Invalid argument at times) as false, not as an exception, so
    // without this line a failed read leaves no trace.
    private async Task<bool> ReadIntoAsync(AsyncFtpClient client, Stream destination, string path, CancellationToken cancellationToken)
    {
        if (await client.DownloadStream(destination, path, token: cancellationToken))
        {
            return true;
        }

        logger.LogWarning("FTP download of {Path} from {Host}:{Port} failed without an error from the console", path, options.Host, options.FtpPort);
        return false;
    }

    public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken) =>
        RunAsync(
            async client =>
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                var status = await client.UploadBytes(bytes, path, FtpRemoteExists.NoCheck, true, null, cancellationToken);
                EnsureUploaded(status, path);
                return true;
            },
            cancellationToken);

    public Task UploadAsync(Stream content, string path, IProgress<long>? progress, CancellationToken cancellationToken) =>
        RunAsync(
            async client =>
            {
                var relay = progress is null ? null : new InlineProgress<FtpProgress>(p => progress.Report(p.TransferredBytes));
                var status = await client.UploadStream(content, path, FtpRemoteExists.NoCheck, true, relay, cancellationToken);
                EnsureUploaded(status, path);

                // Chunk reports arrive synchronously (InlineProgress), so this final report cannot be overtaken.
                if (content.CanSeek)
                {
                    progress?.Report(content.Length);
                }

                return true;
            },
            cancellationToken,
            Lane.Bulk);

    public Task DownloadToAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken) =>
        RunAsync(
            async client =>
            {
                var relay = progress is null ? null : new InlineProgress<FtpProgress>(p => progress.Report(p.TransferredBytes));
                if (!await client.DownloadStream(destination, path, progress: relay, token: cancellationToken))
                {
                    logger.LogWarning("FTP download of {Path} from {Host}:{Port} failed without an error from the console", path, options.Host, options.FtpPort);
                    throw new IOException($"Downloading {path} from the console failed.");
                }

                if (destination.CanSeek)
                {
                    progress?.Report(destination.Length);
                }

                return true;
            },
            cancellationToken,
            Lane.Bulk);

    public Task MoveAsync(string from, string to, CancellationToken cancellationToken) =>
        RunAsync(
            async client =>
            {
                // Keep an existing destination under a backup name until the rename succeeds, so a failed or
                // cancelled move never leaves the console without the old file.
                string? backup = null;
                var parked = false;
                if (await FindAsync(client, to, cancellationToken) is { Type: FtpObjectType.File })
                {
                    backup = $"{to}.{Guid.NewGuid():N}.bak";
                }

                try
                {
                    if (backup is not null)
                    {
                        // Inside the try: a rename that moved the file and then reported failure can be undone below.
                        await client.Rename(to, backup, cancellationToken);
                        parked = true;
                    }

                    await client.Rename(from, to, cancellationToken);
                }
                catch (Exception moveError)
                {
                    if (backup is null)
                    {
                        throw;
                    }

                    Exception? restoreError = null;
                    try
                    {
                        await client.Rename(backup, to, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        restoreError = ex;
                    }

                    if (restoreError is null)
                    {
                        throw;
                    }

                    // A rename that never reported success may have left the destination where it was. Confirming that
                    // keeps the original failure, including cancellation, instead of wrapping it.
                    if (!parked && await DestinationUnmovedAsync(client, to, backup))
                    {
                        throw;
                    }

                    var where = parked
                        ? $"The previous {to} is at {backup}."
                        : $"The previous {to} may be at {backup} if that rename took effect.";
                    throw new IOException(
                        $"Moving {from} to {to} failed: {moveError.Message} {where} Rename that file back to {to} if it is there. Restoring it failed: {restoreError.Message}",
                        new AggregateException(moveError, restoreError));
                }

                if (backup is not null)
                {
                    // The move already succeeded. A leftover backup is harmless clutter and must not fail it.
                    try
                    {
                        await client.DeleteFile(backup, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is FtpException or SocketException or TimeoutException or IOException)
                    {
                        logger.LogWarning(ex, "Moved {To} on the console, but could not delete the backup {Backup}", to, backup);
                    }
                }

                return true;
            },
            cancellationToken);

    public Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken) =>
        RunAsync(
            async client =>
            {
                var trimmed = to.TrimEnd('/');
                var parent = trimmed[..trimmed.LastIndexOf('/')];
                if (parent.Length > 0 && await TryListAsync(client, parent, cancellationToken) is null)
                {
                    // CreateDirectory walks the path and makes every missing level; ftpsrv answers MKD per level.
                    await client.CreateDirectory(parent, true, cancellationToken);
                }

                await client.Rename(from.TrimEnd('/'), trimmed, cancellationToken);
                return true;
            },
            cancellationToken);

    public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
        RunAsync(
            async client =>
            {
                if (await FindAsync(client, path, cancellationToken) is { Type: FtpObjectType.File })
                {
                    await client.DeleteFile(path, cancellationToken);
                }

                return true;
            },
            cancellationToken);

    /// <summary>
    /// True when the destination is still listed and the backup name is not. A failed listing is not confirmation.
    /// </summary>
    private static async Task<bool> DestinationUnmovedAsync(AsyncFtpClient client, string to, string backup)
    {
        try
        {
            var destination = to.TrimEnd('/');
            var split = destination.LastIndexOf('/');
            if (split < 0 || destination.Length == 0)
            {
                return false;
            }

            var parent = split == 0 ? "/" : destination[..split];
            var name = destination[(split + 1)..];
            var backupName = backup[(backup.LastIndexOf('/') + 1)..];
            var items = await TryListAsync(client, parent, CancellationToken.None);
            if (items is null)
            {
                return false;
            }

            var remains = items.Any(item => item.Type == FtpObjectType.File && string.Equals(item.Name, name, StringComparison.Ordinal));
            var backupAbsent = !items.Any(item => item.Type == FtpObjectType.File && string.Equals(item.Name, backupName, StringComparison.Ordinal));
            return remains && backupAbsent;
        }
        catch (Exception ex) when (ex is FtpException or SocketException or TimeoutException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    // ftpsrv answers MLSD only. It has no NLST, and it does not advertise SIZE or MDTM, so FluentFTP's own
    // FileExists/DirectoryExists fail with 502. It also ignores the path argument of MLSD and lists the working
    // folder, so listings change into the folder first and send no path. Existence is read from the parent's listing.
    // Uploads use NoCheck for the same reason: FluentFTP's other modes look the file up with NLST first. STOR replaces
    // an existing file, so nothing is lost.
    private static async Task<FtpListItem?> FindAsync(AsyncFtpClient client, string path, CancellationToken cancellationToken)
    {
        var trimmed = path.TrimEnd('/');
        var split = trimmed.LastIndexOf('/');
        if (split < 0 || trimmed.Length == 0)
        {
            return null;
        }

        var parent = split == 0 ? "/" : trimmed[..split];
        var name = trimmed[(split + 1)..];
        var items = await TryListAsync(client, parent, cancellationToken);
        return items?.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
    }

    /// <summary>A missing folder is a normal answer (550), not a failure of the console.</summary>
    private static async Task<FtpListItem[]?> TryListAsync(AsyncFtpClient client, string path, CancellationToken cancellationToken)
    {
        try
        {
            await client.SetWorkingDirectory(path, cancellationToken);
            return await client.GetListing(path, FtpListOption.Auto | FtpListOption.NoPath, cancellationToken);
        }
        catch (FtpCommandException ex) when (ex.CompletionCode is "550" or "450")
        {
            return null;
        }
    }

    private static void EnsureUploaded(FtpStatus status, string path)
    {
        if (status == FtpStatus.Failed)
        {
            throw new IOException($"Uploading {path} to the console failed.");
        }
    }

    /// <summary>Uploads take the bulk lane; everything else takes the control lane. Each lane runs one operation at a time.</summary>
    private enum Lane
    {
        Control,
        Bulk,
    }

    private async Task<T> RunAsync<T>(Func<AsyncFtpClient, Task<T>> operation, CancellationToken cancellationToken, Lane lane = Lane.Control, [CallerMemberName] string name = "")
    {
        var slot = lane == Lane.Bulk ? _bulk : _control;
        var queued = Stopwatch.GetTimestamp();
        var initialHolder = slot.Holder;
        try
        {
            await slot.Gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The caller (for example the status refresh budget) gave up while another operation held this lane.
            logger.LogInformation(
                "FTP {Operation} on {Host}:{Port} gave up after {WaitedMs} ms waiting behind {Holder} on the {Lane} lane",
                name, options.Host, options.FtpPort, (long)Stopwatch.GetElapsedTime(queued).TotalMilliseconds, Describe(slot.Holder), LaneName(lane));
            throw;
        }

        var waited = Stopwatch.GetElapsedTime(queued);
        if (waited >= SlowGateWait)
        {
            logger.LogInformation(
                "FTP {Operation} on {Host}:{Port} waited {WaitedMs} ms on the {Lane} lane (first behind {Holder})",
                name, options.Host, options.FtpPort, (long)waited.TotalMilliseconds, LaneName(lane), initialHolder?.Name ?? "another operation");
        }

        var started = Stopwatch.GetTimestamp();
        slot.Holder = new Holder(name, started);
        try
        {
            await using var client = new AsyncFtpClient(options.Host, "anonymous", "anonymous", options.FtpPort, CreateConfig());
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(ConnectTimeout);
            try
            {
                await client.Connect(connectTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "FTP {Operation} on {Host}:{Port} timed out connecting after {ElapsedMs} ms",
                    name, options.Host, options.FtpPort, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                throw new ConsoleUnreachableException($"Timed out connecting to {options.Host}:{options.FtpPort}.");
            }

            return await operation(client);
        }
        catch (Exception ex) when (ex is FtpException or SocketException or TimeoutException or IOException
                                       && ex is not ConsoleUnreachableException)
        {
            // The console answered and refused (FtpException) or the connection broke: either way the operation, how
            // long it ran and the underlying error are what is needed to tell a sleeping console from a busy one.
            logger.Log(
                ex is FtpException ? LogLevel.Warning : LogLevel.Information,
                ex,
                "FTP {Operation} on {Host}:{Port} failed after {ElapsedMs} ms ({ErrorType}: {Error})",
                name, options.Host, options.FtpPort, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, ex.GetType().Name, ex.Message);

            throw new ConsoleUnreachableException($"FTP to {options.Host}:{options.FtpPort} failed: {ex.Message}", ex);
        }
        finally
        {
            slot.Holder = null;
            slot.Gate.Release();
        }
    }

    private static string LaneName(Lane lane) => lane == Lane.Bulk ? "bulk" : "control";

    /// <summary>The operation that held the slot when it was read. Only call this with the current holder: the running time is measured now.</summary>
    private static string Describe(Holder? holder) =>
        holder is null ? "another operation" : $"{holder.Name} (running for {(long)Stopwatch.GetElapsedTime(holder.Started).TotalMilliseconds} ms)";

    private sealed class LaneSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public Holder? Holder;
    }

    private sealed record Holder(string Name, long Started);

    private static FtpConfig CreateConfig() => new()
    {
        ConnectTimeout = (int)ConnectTimeout.TotalMilliseconds,
        DataConnectionConnectTimeout = (int)ConnectTimeout.TotalMilliseconds,
        ReadTimeout = 20_000,
        RetryAttempts = 1,
        // ftpsrv speaks plain FTP, and its listings are Unix style.
        EncryptionMode = FtpEncryptionMode.None,
        DataConnectionType = FtpDataConnectionType.AutoPassive,
        SocketKeepAlive = false,
    };

    /// <summary>Invokes the callback on the reporting thread instead of posting it, so reports stay ordered.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

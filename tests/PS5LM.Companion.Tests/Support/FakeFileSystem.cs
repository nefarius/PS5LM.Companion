using System.Text;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Tests.Support;

/// <summary>An in-memory console file system with the failure modes of a real ftpsrv.</summary>
internal sealed class FakeFileSystem : IPS5FileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _modified = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "/" };

    /// <summary>Makes every call throw <see cref="ConsoleUnreachableException"/>.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Leaves the file in place, as a console that accepted DELE but still lists it.</summary>
    public bool IgnoreDeletes { get; set; }

    /// <summary>Makes uploads fail midway, after reporting some progress.</summary>
    public bool FailUploads { get; set; }

    /// <summary>Runs after a directory move succeeds, to simulate the console reacting to the new folder.</summary>
    public Action? AfterMove { get; set; }

    /// <summary>Paths that are listed but whose download fails, as an ftpsrv that answers RETR with 550.</summary>
    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    /// <summary>How many times <see cref="ReadTextAsync"/> was called for each path.</summary>
    public Dictionary<string, int> ReadCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs before a text read looks the path up, so a test can create the file on a later read.</summary>
    public Action<string>? BeforeRead { get; set; }

    public bool Has(string path) => _files.ContainsKey(path);

    public string? Peek(string path) => _files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>Folders whose listing fails with 550: they and their files look absent, as in FtpFileSystem.</summary>
    public HashSet<string> Unlistable { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    /// <summary>Paths passed to <see cref="WriteTextAsync"/>, in order.</summary>
    public List<string> Writes { get; } = [];

    /// <summary>Renames passed to <see cref="MoveAsync"/>, in order.</summary>
    public List<(string From, string To)> Moves { get; } = [];

    public FakeFileSystem AddDirectory(string path)
    {
        var current = string.Empty;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            _directories.Add(current);
        }

        return this;
    }

    public FakeFileSystem AddFile(string path, string content = "", DateTimeOffset? modified = null) =>
        AddFile(path, Encoding.UTF8.GetBytes(content), modified);

    public FakeFileSystem AddFile(string path, byte[] content, DateTimeOffset? modified = null)
    {
        AddDirectory(path[..path.LastIndexOf('/')]);
        _files[path] = content;
        if (modified is { } when)
        {
            _modified[path] = when;
        }
        else
        {
            _modified.Remove(path);
        }

        return this;
    }

    public string? Text(string path) => _files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        Run(() =>
        {
            // Like FtpFileSystem: a folder exists when its parent's listing shows it, whether or not it can be listed itself.
            var trimmed = path.TrimEnd('/');
            return trimmed.Length == 0 || (!Unlistable.Contains(trimmed[..trimmed.LastIndexOf('/')]) && _directories.Contains(trimmed));
        });

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        Run(() => _files.ContainsKey(path) && !Unlistable.Contains(path[..path.LastIndexOf('/')]));

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken) =>
        await TryListAsync(path, cancellationToken) ?? [];

    public Task<IReadOnlyList<RemoteEntry>?> TryListAsync(string path, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<RemoteEntry>?>(() =>
        {
            var trimmed = path.TrimEnd('/');
            if (Unlistable.Contains(trimmed) || (trimmed.Length > 0 && !_directories.Contains(trimmed)))
            {
                return null;
            }

            var prefix = trimmed + "/";
            var files = _files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/'))
                .Select(f => new RemoteEntry(
                    f.Key[prefix.Length..],
                    f.Key,
                    false,
                    f.Value.Length,
                    _modified.TryGetValue(f.Key, out var modified) ? modified : null));
            var dirs = _directories
                .Where(d => d.StartsWith(prefix, StringComparison.Ordinal) && !d[prefix.Length..].Contains('/'))
                .Select(d => new RemoteEntry(d[prefix.Length..], d, true, 0, null));
            return files.Concat(dirs).ToList();
        });

    public Task<string?> ReadTextAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        ReadCounts[path] = ReadCounts.GetValueOrDefault(path) + 1;
        BeforeRead?.Invoke(path);
        return Run(() => !Unreadable.Contains(path) && _files.TryGetValue(path, out var bytes) && bytes.Length <= maxBytes ? Encoding.UTF8.GetString(bytes) : null);
    }

    public Task<byte[]?> ReadBytesAsync(string path, int maxBytes, CancellationToken cancellationToken) =>
        Run(() => !Unreadable.Contains(path) && _files.TryGetValue(path, out var bytes) && bytes.Length <= maxBytes ? bytes : null);

    public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken) =>
        Run(() =>
        {
            AddFile(path, content);
            Writes.Add(path);
            return true;
        });

    /// <summary>Signalled when an upload starts; lets a test observe that it is running.</summary>
    public TaskCompletionSource? UploadStarted { get; set; }

    /// <summary>Holds every upload until released.</summary>
    public TaskCompletionSource? UploadGate { get; set; }

    /// <summary>How many times <see cref="DownloadToAsync"/> started.</summary>
    public int Downloads => _downloads;

    /// <summary>Downloads that have started and not yet returned, including one waiting on <see cref="DownloadGate"/>.</summary>
    public int ActiveDownloads => _activeDownloads;

    private int _downloads;

    private int _activeDownloads;

    /// <summary>Signalled when a download starts.</summary>
    public TaskCompletionSource? DownloadStarted { get; set; }

    /// <summary>Holds every download until released.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    /// <summary>Makes a download fail before any bytes are written.</summary>
    public bool FailDownloads { get; set; }

    public async Task UploadAsync(Stream content, string path, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable();
        UploadStarted?.TrySetResult();
        if (UploadGate is not null)
        {
            await UploadGate.Task.WaitAsync(cancellationToken);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            progress?.Report(buffer.Length);
            if (FailUploads)
            {
                throw new ConsoleUnreachableException("The connection dropped during the upload.");
            }
        }

        AddFile(path, buffer.ToArray());
    }

    public async Task DownloadToAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable();
        Interlocked.Increment(ref _downloads);
        DownloadStarted?.TrySetResult();
        Interlocked.Increment(ref _activeDownloads);
        try
        {
            if (DownloadGate is not null)
            {
                await DownloadGate.Task.WaitAsync(cancellationToken);
            }

            if (FailDownloads)
            {
                throw new ConsoleUnreachableException("The connection dropped during the download.");
            }

            if (Unreadable.Contains(path) || !_files.TryGetValue(path, out var bytes))
            {
                throw new IOException($"Downloading {path} from the console failed.");
            }

            await destination.WriteAsync(bytes, cancellationToken);
            progress?.Report(bytes.Length);
        }
        finally
        {
            Interlocked.Decrement(ref _activeDownloads);
        }
    }

    public Task MoveAsync(string from, string to, CancellationToken cancellationToken) =>
        Run(() =>
        {
            if (!_files.Remove(from, out var bytes))
            {
                throw new IOException($"{from} does not exist.");
            }

            AddFile(to, bytes);
            Moves.Add((from, to));
            return true;
        });

    public Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken) =>
        Run(() =>
        {
            var source = from.TrimEnd('/') + "/";
            if (!_directories.Contains(from.TrimEnd('/')) || _directories.Contains(to.TrimEnd('/')))
            {
                throw new IOException($"Cannot move {from} to {to}.");
            }

            var moved = _files.Where(f => f.Key.StartsWith(source, StringComparison.Ordinal)).ToList();
            foreach (var (key, bytes) in moved)
            {
                _files.Remove(key);
                AddFile(to.TrimEnd('/') + "/" + key[source.Length..], bytes);
            }

            foreach (var dir in _directories.Where(d => d == from.TrimEnd('/') || d.StartsWith(source, StringComparison.Ordinal)).ToList())
            {
                _directories.Remove(dir);
            }

            AddDirectory(to);
            AfterMove?.Invoke();
            return true;
        });

    public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
        Run(() => IgnoreDeletes || _files.Remove(path));

    private Task<T> Run<T>(Func<T> action)
    {
        ThrowIfUnreachable();
        return Task.FromResult(action());
    }

    private void ThrowIfUnreachable()
    {
        if (Unreachable)
        {
            throw new ConsoleUnreachableException("The console is unreachable.");
        }
    }
}

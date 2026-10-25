using System.Text;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Tests.Support;

/// <summary>An in-memory console file system with the failure modes of a real ftpsrv.</summary>
internal sealed class FakeFileSystem : IPS5FileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { "/" };

    /// <summary>Makes every call throw <see cref="ConsoleUnreachableException"/>.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Leaves the file in place, as a console that accepted DELE but still lists it.</summary>
    public bool IgnoreDeletes { get; set; }

    /// <summary>Makes uploads fail midway, after reporting some progress.</summary>
    public bool FailUploads { get; set; }

    public IReadOnlyDictionary<string, byte[]> Files => _files;

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

    public FakeFileSystem AddFile(string path, string content = "") => AddFile(path, Encoding.UTF8.GetBytes(content));

    public FakeFileSystem AddFile(string path, byte[] content)
    {
        AddDirectory(path[..path.LastIndexOf('/')]);
        _files[path] = content;
        return this;
    }

    public string? Text(string path) => _files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
        Run(() => _directories.Contains(path.TrimEnd('/')));

    public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        Run(() => _files.ContainsKey(path));

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken) =>
        Run<IReadOnlyList<RemoteEntry>>(() =>
        {
            var prefix = path.TrimEnd('/') + "/";
            var files = _files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && !f.Key[prefix.Length..].Contains('/'))
                .Select(f => new RemoteEntry(f.Key[prefix.Length..], f.Key, false, f.Value.Length, null));
            var dirs = _directories
                .Where(d => d.StartsWith(prefix, StringComparison.Ordinal) && !d[prefix.Length..].Contains('/'))
                .Select(d => new RemoteEntry(d[prefix.Length..], d, true, 0, null));
            return files.Concat(dirs).ToList();
        });

    public Task<string?> ReadTextAsync(string path, int maxBytes, CancellationToken cancellationToken) =>
        Run(() => _files.TryGetValue(path, out var bytes) && bytes.Length <= maxBytes ? Encoding.UTF8.GetString(bytes) : null);

    public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken) =>
        Run(() =>
        {
            AddFile(path, content);
            return true;
        });

    /// <summary>Signalled when an upload starts; lets a test observe that it is running.</summary>
    public TaskCompletionSource? UploadStarted { get; set; }

    /// <summary>Holds every upload until released.</summary>
    public TaskCompletionSource? UploadGate { get; set; }

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

    public Task MoveAsync(string from, string to, CancellationToken cancellationToken) =>
        Run(() =>
        {
            if (!_files.Remove(from, out var bytes))
            {
                throw new IOException($"{from} does not exist.");
            }

            AddFile(to, bytes);
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

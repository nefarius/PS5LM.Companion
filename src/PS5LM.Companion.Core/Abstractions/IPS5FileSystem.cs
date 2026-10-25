using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Abstractions;

/// <summary>Thrown when the console's FTP service cannot be reached or drops the connection.</summary>
public sealed class ConsoleUnreachableException : Exception
{
    public ConsoleUnreachableException()
    {
    }

    public ConsoleUnreachableException(string message)
        : base(message)
    {
    }

    public ConsoleUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The console's file system as reached over ftpsrv. Implementations translate connection failures into
/// <see cref="ConsoleUnreachableException"/> and never leak the FTP library's types.
/// </summary>
public interface IPS5FileSystem
{
    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken);

    Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken);

    /// <summary>Lists a directory; an empty list when it does not exist.</summary>
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads a text file of at most <paramref name="maxBytes"/>; <see langword="null"/> when missing or larger.</summary>
    Task<string?> ReadTextAsync(string path, int maxBytes, CancellationToken cancellationToken);

    /// <summary>Creates or replaces a small text file.</summary>
    Task WriteTextAsync(string path, string content, CancellationToken cancellationToken);

    /// <summary>Uploads a stream to <paramref name="path"/>, creating the directory, reporting bytes sent.</summary>
    Task UploadAsync(Stream content, string path, IProgress<long>? progress, CancellationToken cancellationToken);

    /// <summary>Renames a file, replacing the target.</summary>
    Task MoveAsync(string from, string to, CancellationToken cancellationToken);

    /// <summary>Renames a directory to a path that does not exist yet, creating the target's parent.</summary>
    Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken);

    Task DeleteAsync(string path, CancellationToken cancellationToken);
}

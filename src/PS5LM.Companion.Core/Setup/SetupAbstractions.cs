using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Setup;

/// <summary>A payload Payload Manager lists. 0.6.x reports a slot; 0.5.x only a file path.</summary>
/// <param name="Slot">Launch identity (0.6.x), or empty on 0.5.x.</param>
/// <param name="Id">Catalog id when Payload Manager reports one.</param>
/// <param name="FileName">The stored file name, for example <c>elfldr.elf</c>.</param>
/// <param name="Path">The stored path when reported.</param>
public sealed record ManagedPayload(string Slot, string? Id, string FileName, string? Path)
{
    /// <summary>What to pass to the load call: the slot when there is one, else the path.</summary>
    public string LaunchTarget => string.IsNullOrEmpty(Slot) ? Path ?? FileName : Slot;
}

/// <summary>The autoload settings Payload Manager keeps.</summary>
public sealed record AutoloadConfig(bool Enabled, string List);

/// <summary>Payload Manager's administration surface, in the forms that 0.5.x and 0.6.x share and the 0.6.x extras.</summary>
public interface IPayloadManagerAdmin
{
    /// <summary>The reported version text, or <see langword="null"/> when nothing answered.</summary>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ManagedPayload>> ListPayloadsAsync(CancellationToken cancellationToken);

    /// <summary>Stores a payload file; the name decides the slot (and, on 0.5.x, the folder).</summary>
    Task UploadAsync(string fileName, Stream content, CancellationToken cancellationToken);

    Task<PayloadLaunchResult> LoadAsync(string target, CancellationToken cancellationToken);

    /// <summary>Process names from <c>/processes_list</c>, or <see langword="null"/> when it could not be read.</summary>
    Task<IReadOnlySet<string>?> GetProcessesAsync(CancellationToken cancellationToken);

    /// <summary>Process id to name from <c>/processes_list</c>, or <see langword="null"/> when it could not be read.</summary>
    Task<IReadOnlyDictionary<int, string>?> GetProcessIdsAsync(CancellationToken cancellationToken);

    /// <summary>Asks the manager to terminate a process (<c>/process_kill</c>); false when it refused.</summary>
    Task<bool> KillProcessAsync(int pid, CancellationToken cancellationToken);

    Task<AutoloadConfig?> GetAutoloadAsync(CancellationToken cancellationToken);

    Task SetAutoloadAsync(bool enabled, string list, CancellationToken cancellationToken);

    /// <summary>Asks the running manager to exit.</summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}

/// <summary>Downloads an artifact and proves its SHA-256 before returning a local file.</summary>
public interface IArtifactStore
{
    /// <summary>Returns the path of a verified local copy; throws <see cref="InvalidDataException"/> on a hash mismatch.</summary>
    Task<string> FetchAsync(string url, string sha256, CancellationToken cancellationToken);
}

/// <summary>Checks services on the one console being set up.</summary>
public interface IConsoleProbe
{
    Task<bool> PortOpenAsync(int port, CancellationToken cancellationToken);

    /// <summary>True when <c>GET http://host:port{path}</c> answers 2xx with an HTML body.</summary>
    Task<bool> HtmlAsync(int port, string path, CancellationToken cancellationToken);

    /// <summary>True when an empty <c>POST http://host:port{path}</c> answers 2xx.</summary>
    Task<bool> PostAsync(int port, string path, CancellationToken cancellationToken);
}

/// <summary>Sends an ELF straight to elfldr.</summary>
public interface IElfldrSender
{
    Task SendAsync(string filePath, CancellationToken cancellationToken);

    /// <summary>
    /// True when elfldr takes a connection and keeps it open. A wedged elfldr still accepts the connection but
    /// closes it at once, so a port check alone is not enough before something irreversible is stopped.
    /// </summary>
    Task<bool> AcceptsAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

/// <summary>What setup tells and asks the person running it.</summary>
public interface ISetupUi
{
    bool CanPrompt { get; }

    void Info(string message);

    bool Confirm(string question);
}

/// <summary>Everything setup needs to touch one console.</summary>
public sealed record SetupServices(
    IPayloadManagerAdmin Manager,
    IArtifactStore Artifacts,
    IPS5FileSystem Files,
    IConsoleProbe Probe,
    IElfldrSender Elfldr,
    IAppControlProvider? VoidShell,
    ISetupUi Ui,
    TimeProvider Time);

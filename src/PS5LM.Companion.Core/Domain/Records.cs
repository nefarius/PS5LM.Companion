namespace PS5LM.Companion.Core.Domain;

/// <summary>One entry of a remote directory listing.</summary>
public sealed record RemoteEntry(string Name, string FullPath, bool IsDirectory, long Size, DateTimeOffset? Modified);

/// <summary>
/// A directory as reached over FTP (<see cref="FtpPath"/>) and as the app sees it (<see cref="AppPath"/>).
/// The two differ for PS5LM Plus when it runs in its own sandbox: FTP reaches it under
/// <c>/mnt/sandbox/PPSA99581_000/download0</c> while the app sees <c>/download0</c>.
/// </summary>
public sealed record StorageRoot(string Name, string FtpPath, string AppPath, bool Writable)
{
    /// <summary>Maps a path below <see cref="AppPath"/> to the FTP path, or <see langword="null"/> when outside.</summary>
    public string? ToFtpPath(string appPath) => Remap(appPath, AppPath, FtpPath);

    /// <summary>Maps a path below <see cref="FtpPath"/> to the app's view, or <see langword="null"/> when outside.</summary>
    public string? ToAppPath(string ftpPath) => Remap(ftpPath, FtpPath, AppPath);

    private static string? Remap(string path, string from, string to)
    {
        if (path.Equals(from, StringComparison.Ordinal))
        {
            return to;
        }

        var prefix = from.TrimEnd('/') + "/";
        return path.StartsWith(prefix, StringComparison.Ordinal) ? to.TrimEnd('/') + "/" + path[prefix.Length..] : null;
    }
}

/// <summary>A GGUF file on the console.</summary>
/// <param name="FileName">The file name, e.g. <c>Qwen3.5-9B-Q4_K_M.gguf</c>.</param>
/// <param name="FtpPath">Where FTP reaches it.</param>
/// <param name="AppPath">The path to write into the <c>load</c> command file: how the app sees the file.</param>
/// <param name="SizeBytes">Size in bytes.</param>
/// <param name="Location">The storage root's display name (e.g. "Internal storage", "USB 0").</param>
/// <param name="Loaded">Whether llama-server reports this model as the loaded one.</param>
public sealed record ModelFile(string FileName, string FtpPath, string AppPath, long SizeBytes, string Location, bool Loaded);

/// <summary>What the companion can do against the console right now. Features degrade independently.</summary>
public sealed record ConsoleCapabilities(
    bool Ftp,
    bool ModelControl,
    bool ModelUpload,
    bool SettingsRead,
    bool Telemetry,
    bool Sensors)
{
    public static ConsoleCapabilities None { get; } = new(false, false, false, false, false, false);
}

/// <summary>Counters and derived speeds from llama-server's <c>/metrics</c>.</summary>
public sealed record TelemetrySnapshot(
    double TokensGenerated,
    double PromptTokens,
    double RequestsProcessing,
    double GenerationTokensPerSecond,
    double PromptTokensPerSecond,
    DateTimeOffset SampledAt);

/// <summary>Free and total space of a console drive, from ps5-exporter.</summary>
public sealed record DriveSpace(string MountPoint, long TotalBytes, long FreeBytes);

/// <summary>Console sensors from ps5-exporter; <see langword="null"/> where it could not be read.</summary>
public sealed record ConsoleSensors(
    double? SocTemperatureC,
    double? CpuTemperatureC,
    double? SocPowerWatts,
    double? FanDuty,
    IReadOnlyList<DriveSpace> Drives);

/// <summary>The normalised state the dashboard and REST API present.</summary>
public sealed record CompanionStatus(
    CompanionLifecycle Lifecycle,
    PS5Variant Variant,
    string? ControlRoot,
    bool CommandPending,
    string? LoadedModel,
    ConsoleCapabilities Capabilities,
    TelemetrySnapshot? Telemetry,
    ConsoleSensors? Sensors,
    string? Message,
    DateTimeOffset UpdatedAt)
{
    public static CompanionStatus Initial { get; } = new(
        CompanionLifecycle.Disconnected,
        PS5Variant.Unknown,
        null,
        false,
        null,
        ConsoleCapabilities.None,
        null,
        null,
        "Waiting for the first status check.",
        DateTimeOffset.MinValue);
}

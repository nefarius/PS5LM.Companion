using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Telemetry;

namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Combines FTP discovery, the llama-server probe and ps5-exporter into one <see cref="CompanionStatus"/>.
/// Each source degrades on its own: the companion stays useful before port 8081 exists.
/// </summary>
public sealed class CompanionStatusService(
    PS5Options options,
    IPS5FileSystem fileSystem,
    ILlamaServerProbe llama,
    IConsoleSensorProbe sensorProbe,
    IPayloadCatalog payloads,
    IElfldrProbe elfldr,
    LoadTracker loadTracker,
    TimeProvider time)
{
    private readonly TelemetryCalculator _telemetry = new();
    private Installation? _installation;
    private DateTimeOffset _installationAt = DateTimeOffset.MinValue;

    /// <summary>The installation found by the last successful lookup, for services that act on the console.</summary>
    public Installation? LastInstallation => _installation;

    /// <summary>Forgets the cached installation, e.g. after the user changed the console settings.</summary>
    public void InvalidateInstallation() => _installationAt = DateTimeOffset.MinValue;

    public async Task<CompanionStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        var llamaTask = llama.ProbeAsync(cancellationToken);
        var sensorTask = sensorProbe.ReadAsync(cancellationToken);
        var catalogTask = payloads.ReadAsync(cancellationToken);
        var elfldrTask = elfldr.IsListeningAsync(cancellationToken);
        var ftp = await ReadFtpAsync(now, cancellationToken);
        var probe = await llamaTask;
        var sensors = await sensorTask;
        var catalog = await catalogTask;
        var elfldrListening = await elfldrTask;

        var telemetry = probe.State == LlamaServerState.Serving ? _telemetry.Update(probe.MetricsText, now) : ResetTelemetry();
        if (probe.State == LlamaServerState.Serving)
        {
            loadTracker.Clear();
        }

        var lifecycle = DetermineLifecycle(probe, telemetry, ftp.Reachable, loadTracker.IsLoading(TimeSpan.FromSeconds(options.LoadGraceSeconds)));

        var capabilities = new ConsoleCapabilities(
            Ftp: ftp.Reachable,
            ModelControl: ftp.Installation?.StateRoot is not null,
            ModelUpload: ftp.Installation?.StateRoot is { Writable: true },
            SettingsRead: ftp.SettingsPresent,
            Telemetry: telemetry is not null,
            Sensors: sensors is not null);

        return new CompanionStatus(
            lifecycle,
            ftp.Installation?.Profile?.Variant ?? PS5Variant.Unknown,
            ftp.Installation?.StateRoot?.FtpPath,
            ftp.CommandPending,
            probe.LoadedModel,
            capabilities,
            telemetry,
            sensors,
            Message(lifecycle, ftp),
            now,
            catalog.Available,
            catalog.Available ? null : catalog.Message,
            PayloadDependencies.Evaluate(catalog, ftp.Reachable, sensors is not null, elfldrListening, options.ElfldrPort));
    }

    private static CompanionLifecycle DetermineLifecycle(
        LlamaProbeResult probe,
        TelemetrySnapshot? telemetry,
        bool ftpReachable,
        bool loadRequested)
    {
        switch (probe.State)
        {
            case LlamaServerState.Serving:
                return telemetry is { RequestsProcessing: > 0 } ? CompanionLifecycle.Generating : CompanionLifecycle.Ready;
            case LlamaServerState.Loading:
                return CompanionLifecycle.Loading;
        }

        if (!ftpReachable)
        {
            return CompanionLifecycle.Disconnected;
        }

        return loadRequested ? CompanionLifecycle.Loading : CompanionLifecycle.AppAvailableNoModel;
    }

    private static string? Message(CompanionLifecycle lifecycle, FtpView ftp) => lifecycle switch
    {
        CompanionLifecycle.Disconnected => ftp.Failure ?? "The console's FTP service is not reachable.",
        CompanionLifecycle.AppAvailableNoModel => ftp.Installation?.Message
            ?? (ftp.CommandPending
                ? "A command is waiting; the app has not picked it up (is it running?)."
                : "No model is loaded."),
        _ => null,
    };

    private TelemetrySnapshot? ResetTelemetry()
    {
        _telemetry.Reset();
        return null;
    }

    private async Task<FtpView> ReadFtpAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            if (_installation is null || now - _installationAt >= TimeSpan.FromSeconds(options.DiscoveryCacheSeconds))
            {
                _installation = await InstallationDetector.DetectAsync(fileSystem, options.Variant, cancellationToken);
                _installationAt = now;
            }

            var root = _installation.StateRoot;
            if (root is null)
            {
                return new FtpView(true, _installation, false, false, null);
            }

            var pending = await fileSystem.FileExistsAsync($"{root.FtpPath}/{VariantProfile.LoadCommandFile}", cancellationToken)
                          || await fileSystem.FileExistsAsync($"{root.FtpPath}/{VariantProfile.UnloadCommandFile}", cancellationToken);
            var settings = await fileSystem.FileExistsAsync($"{root.FtpPath}/settings.json", cancellationToken);
            return new FtpView(true, _installation, pending, settings, null);
        }
        catch (ConsoleUnreachableException ex)
        {
            _installation = null;
            return new FtpView(false, null, false, false, ex.Message);
        }
    }

    private sealed record FtpView(bool Reachable, Installation? Installation, bool CommandPending, bool SettingsPresent, string? Failure);
}

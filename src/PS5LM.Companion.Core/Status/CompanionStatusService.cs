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
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Installation? _installation;
    private DateTimeOffset _installationAt = DateTimeOffset.MinValue;
    private FtpView? _lastFtp;

    /// <summary>
    /// FTP is the slowest source, and ftpsrv crawls while the app loads a model. The status is published with the
    /// last known FTP view after this long, so the lifecycle reflects llama-server and the sensors in time.
    /// </summary>
    private static readonly TimeSpan FtpBudget = TimeSpan.FromSeconds(6);

    /// <summary>The installation found by the last successful lookup, for services that act on the console.</summary>
    public Installation? LastInstallation => _installation;

    /// <summary>Forgets the cached installation, e.g. after the user changed the console settings.</summary>
    public void InvalidateInstallation() => _installationAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Refreshes the status. Calls are serialized, because the monitor, transfers, model control and payload
    /// launches all refresh, and the telemetry calculator and installation cache are not thread-safe.
    /// </summary>
    public async Task<CompanionStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            return await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<CompanionStatus> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        var llamaTask = llama.ProbeAsync(cancellationToken);
        var sensorTask = sensorProbe.ReadAsync(cancellationToken);
        var catalogTask = payloads.ReadAsync(cancellationToken);
        var elfldrTask = elfldr.IsListeningAsync(cancellationToken);
        var ftp = await ReadFtpWithinBudgetAsync(now, cancellationToken);
        var probe = await llamaTask;
        var sensors = await sensorTask;
        var catalog = await catalogTask;
        var elfldrListening = await elfldrTask;

        var telemetry = probe.State == LlamaServerState.Serving ? _telemetry.Update(probe.MetricsText, now) : ResetTelemetry();
        if (probe.State == LlamaServerState.Serving)
        {
            loadTracker.Clear();
        }

        var loadRequested = loadTracker.IsLoading(TimeSpan.FromSeconds(options.LoadGraceSeconds));

        // A slow FTP read only counts as "still there" while a load is under way: the console is busy loading then.
        var lifecycle = DetermineLifecycle(probe, telemetry, ftp.Reachable || (ftp.Stale && loadRequested), loadRequested);

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

    private async Task<FtpView> ReadFtpWithinBudgetAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(FtpBudget);
        try
        {
            return _lastFtp = await ReadFtpAsync(now, budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Keep what was learned earlier, but report FTP as unverified: nothing was read this time.
            const string slow = "The console's FTP service is slow to answer.";
            return _lastFtp is { } last
                ? last with { Reachable = false, Stale = true, Failure = slow }
                : new FtpView(false, null, false, false, slow, Stale: true);
        }
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

    private sealed record FtpView(bool Reachable, Installation? Installation, bool CommandPending, bool SettingsPresent, string? Failure, bool Stale = false);
}

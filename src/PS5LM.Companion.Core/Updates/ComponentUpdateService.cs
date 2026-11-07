using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Core.Status;

namespace PS5LM.Companion.Core.Updates;

/// <summary>
/// One console's hash comparison against the pinned manifest. Calls are serialized. After each check the
/// latest <see cref="CompanionStatus"/> is republished with a merged <see cref="CompanionStatus.Updates"/>.
/// </summary>
public sealed class ComponentUpdateService
{
    private readonly SetupManifest _manifest;
    private readonly PS5Options _options;
    private readonly SetupServices _services;
    private readonly CompanionStatusStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateGate = new();
    private ComponentUpdateSnapshot _snapshot = ComponentUpdateSnapshot.Empty;
    private IReadOnlyList<ComponentUpdate> _live = [];
    private string? _managerVersion;
    private string? _voidShellSemVer;
    private bool _seenLive;
    private TaskCompletionSource _due = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ComponentUpdateService(
        SetupManifest manifest,
        PS5Options options,
        SetupServices services,
        CompanionStatusStore store,
        TimeProvider time)
    {
        _manifest = manifest;
        _options = options;
        _services = services with { Ui = SilentSetupUi.Instance };
        _store = store;
        _time = time;
    }

    public ComponentUpdateSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>True when a live version changed after the last hash check and a new pass should run soon.</summary>
    public bool IsDue
    {
        get
        {
            lock (_stateGate)
            {
                return _due.Task.IsCompleted;
            }
        }
    }

    /// <summary>Remembers the latest live flags and requests an early hash check when a version string changes.</summary>
    public void NoteLive(IReadOnlyList<ComponentUpdate> live, string? managerVersion, string? voidShellSemVer)
    {
        bool request;
        lock (_stateGate)
        {
            _live = live;
            var changed = !string.Equals(_managerVersion, managerVersion, StringComparison.Ordinal)
                          || !string.Equals(_voidShellSemVer, voidShellSemVer, StringComparison.Ordinal);
            request = changed && _seenLive && _snapshot.CheckedAt is not null;
            _managerVersion = managerVersion;
            _voidShellSemVer = voidShellSemVer;
            _seenLive = true;
            if (request)
            {
                _due.TrySetResult();
            }
        }
    }

    public ComponentUpdateStatus CurrentStatus()
    {
        lock (_stateGate)
        {
            return ComponentUpdates.Merge(_live, _snapshot, PayloadManagerVersion.TryParse(_managerVersion, out _));
        }
    }

    public async Task WaitDueAsync(CancellationToken cancellationToken)
    {
        Task due;
        lock (_stateGate)
        {
            due = _due.Task;
        }

        await due.WaitAsync(cancellationToken);
    }

    public void ClearDue()
    {
        lock (_stateGate)
        {
            if (_due.Task.IsCompleted)
            {
                _due = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public Task<ComponentUpdateStatus> CheckAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => CheckCoreAsync(cancellationToken), cancellationToken);

    /// <summary>
    /// Rewrites the autoload list, countdown and bundled-kstuff switch, then refreshes the published comparison.
    /// Payload and app files are not touched. Callers refuse this while <see cref="BootSettings.IsBusy"/> is true.
    /// </summary>
    public Task<ComponentUpdateStatus> ApplySettingsAsync(CancellationToken cancellationToken) =>
        RunExclusiveAsync(async () =>
        {
            var lines = new List<ReportLine>();
            var inventory = await _services.Manager.ListPayloadsAsync(cancellationToken);
            var resolved = new List<(ManifestPayload Payload, ManagedPayload Stored)>();
            foreach (var payload in _manifest.Payloads)
            {
                if (SetupRunner.FindStored(inventory, payload) is { } stored)
                {
                    resolved.Add((payload, stored));
                }
            }

            await BootSettings.ApplyAutoloadAsync(resolved, _services, (component, status, message) => lines.Add(new ReportLine(component, status, message)), cancellationToken);
            await BootSettings.ApplyKstuffAsync(
                _manifest,
                resolved,
                _services,
                _options.FtpPort,
                new OnionConfigSession(),
                (component, status, message) => lines.Add(new ReportLine(component, status, message)),
                cancellationToken);
            var status = await CheckCoreAsync(cancellationToken);
            var applyFailure = lines.FirstOrDefault(line => line.Status == SetupStatus.Fail)?.Message;
            if (applyFailure is null)
            {
                return status;
            }

            SetSnapshot(current => current with { Checking = false, Failure = applyFailure });
            return Publish();
        }, cancellationToken);

    private async Task<ComponentUpdateStatus> RunExclusiveAsync(Func<Task<ComponentUpdateStatus>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        catch (OperationCanceledException)
        {
            SetSnapshot(current => current with { Checking = false });
            Publish();
            throw;
        }
        catch (Exception ex)
        {
            SetSnapshot(current => current with { Checking = false, Failure = ex.Message, CheckedAt = _time.GetUtcNow() });
            return Publish();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ComponentUpdateStatus> CheckCoreAsync(CancellationToken cancellationToken)
    {
        SetSnapshot(current => current with { Checking = true });
        Publish();

        var options = new UpdateOptions(_options.Host)
        {
            IncludeApps = true,
            IncludeSettings = true,
            CheckOnly = true,
            FtpPort = _options.FtpPort,
            ElfldrPort = _options.ElfldrPort,
            PayloadManagerPort = _options.PayloadManagerBaseAddress().Port,
            VoidShellPort = _options.VoidShellPort,
        };
        var report = await new UpdateRunner(_manifest, options, _services).RunAsync(cancellationToken);
        var failure = report.Lines.FirstOrDefault(line => line.Status == SetupStatus.Fail)?.Message;
        SetSnapshot(current => failure is null
            ? new ComponentUpdateSnapshot(report.Updates, _time.GetUtcNow(), false, null) { Settings = report.Settings }
            : current with { Checking = false, Failure = failure, CheckedAt = _time.GetUtcNow() });
        return Publish();
    }

    private void SetSnapshot(Func<ComponentUpdateSnapshot, ComponentUpdateSnapshot> update)
    {
        lock (_stateGate)
        {
            _snapshot = update(_snapshot);
        }
    }

    private ComponentUpdateStatus Publish()
    {
        var status = CurrentStatus();
        _store.Publish(_store.Current with { Updates = status });
        return status;
    }
}

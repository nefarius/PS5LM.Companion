using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class CompanionStatusServiceTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLlamaProbe _llama = new();
    private readonly FakeSensorProbe _sensors = new();
    private readonly LoadTracker _tracker;
    private readonly PS5Options _options = new() { Host = "ps5" };

    public CompanionStatusServiceTests() => _tracker = new LoadTracker(_time);

    private CompanionStatusService Create(FakeFileSystem fs) =>
        new(_options, fs, _llama, _sensors, new FakePayloadCatalog(), new FakeElfldrProbe(), _tracker, _time);

    [Fact]
    public async Task An_older_payload_manager_is_flagged_on_every_refresh()
    {
        var catalog = new FakePayloadCatalog
        {
            Snapshot = new PayloadCatalogSnapshot(true, true, null, [], new HashSet<string>(), null, "0.6.0"),
        };
        var service = new CompanionStatusService(
            _options, ConsoleFixtures.PS5LMInstalled(), _llama, _sensors, catalog, new FakeElfldrProbe(), _tracker, _time,
            manifest: SetupManifest.LoadEmbedded());

        var status = await service.RefreshAsync(CancellationToken.None);

        var update = Assert.Single(status.Updates!.Outdated);
        Assert.Equal(PayloadDependencies.ManagerId, update.Id);
        Assert.Contains("0.6.0", update.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_older_voidshell_semver_is_flagged_and_a_missing_semver_is_not()
    {
        var older = new FakeAppProvider(AppProviderKind.VoidShell)
        {
            Snapshot = new AppProviderSnapshot(AppProviderKind.VoidShell, true, "V1.1", [], true, null, null, SemVer: "1.0.0-nefarius.1"),
        };
        var upstream = new FakeAppProvider(AppProviderKind.VoidShell)
        {
            Snapshot = new AppProviderSnapshot(AppProviderKind.VoidShell, true, "V1.1", [], true, null, null),
        };
        var manifest = SetupManifest.LoadEmbedded();
        var olderStatus = await new CompanionStatusService(
            _options, ConsoleFixtures.PS5LMInstalled(), _llama, _sensors, new FakePayloadCatalog(), new FakeElfldrProbe(), _tracker, _time,
            [older], manifest).RefreshAsync(CancellationToken.None);
        var upstreamStatus = await new CompanionStatusService(
            _options, ConsoleFixtures.PS5LMInstalled(), _llama, _sensors, new FakePayloadCatalog(), new FakeElfldrProbe(), _tracker, _time,
            [upstream], manifest).RefreshAsync(CancellationToken.None);

        Assert.Contains(olderStatus.Updates!.Outdated, update => update.Id == PayloadDependencies.VoidShell);
        Assert.DoesNotContain(upstreamStatus.Updates!.Outdated, update => update.Id == PayloadDependencies.VoidShell);
    }

    [Fact]
    public async Task The_firmware_comes_from_an_available_VoidShell()
    {
        var voidShell = new FakeAppProvider(AppProviderKind.VoidShell)
        {
            Snapshot = new AppProviderSnapshot(AppProviderKind.VoidShell, true, "V1.1", [], true, null, null, "13.40"),
        };
        var service = new CompanionStatusService(_options, ConsoleFixtures.PS5LMInstalled(), _llama, _sensors, new FakePayloadCatalog(), new FakeElfldrProbe(), _tracker, _time, [voidShell]);

        var status = await service.RefreshAsync(CancellationToken.None);
        voidShell.Snapshot = AppProviderSnapshot.Unavailable(AppProviderKind.VoidShell, "down");
        var offline = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal("13.40", status.FirmwareVersion);
        Assert.Null(offline.FirmwareVersion);
        Assert.Equal(DependencyHealth.Verified, status.Dependencies.Single(row => row.Id == PayloadDependencies.VoidShell).Health);
        Assert.Equal(DependencyHealth.Unknown, offline.Dependencies.Single(row => row.Id == PayloadDependencies.VoidShell).Health);
    }

    [Fact]
    public async Task Overlapping_refreshes_run_one_at_a_time()
    {
        var catalog = new BlockingCatalog();
        var service = new CompanionStatusService(_options, ConsoleFixtures.PS5LMInstalled(), _llama, _sensors, catalog, new FakeElfldrProbe(), _tracker, _time);

        var first = service.RefreshAsync(CancellationToken.None);
        await catalog.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.RefreshAsync(CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(1, catalog.Reads);
        catalog.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, catalog.Reads);
    }

    [Fact]
    public async Task A_cancelled_refresh_waiting_for_the_gate_does_not_block_later_refreshes()
    {
        var catalog = new BlockingCatalog();
        var service = new CompanionStatusService(_options, ConsoleFixtures.PS5LMInstalled(), _llama, _sensors, catalog, new FakeElfldrProbe(), _tracker, _time);
        var first = service.RefreshAsync(CancellationToken.None);
        await catalog.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        var waiting = service.RefreshAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        catalog.Release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await service.RefreshAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_hanging_ftp_service_does_not_delay_the_llama_status()
    {
        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, null, "m.gguf");
        var service = new CompanionStatusService(_options, new HangingFileSystem(), _llama, _sensors, new FakePayloadCatalog(), new FakeElfldrProbe(), _tracker, _time);

        var status = await service.RefreshAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(CompanionLifecycle.Ready, status.Lifecycle);
        Assert.False(status.Capabilities.Ftp);
        Assert.NotEqual(DependencyHealth.Verified, status.Dependencies.Single(row => row.Id == PayloadDependencies.FtpSrv).Health);
    }

    [Fact]
    public async Task A_slow_ftp_service_keeps_a_requested_load_in_the_loading_state()
    {
        _tracker.MarkRequested("m.gguf");
        var service = new CompanionStatusService(_options, new HangingFileSystem(), _llama, _sensors, new FakePayloadCatalog(), new FakeElfldrProbe(), _tracker, _time);

        var status = await service.RefreshAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(CompanionLifecycle.Loading, status.Lifecycle);
        Assert.False(status.Capabilities.Ftp);
    }

    [Fact]
    public async Task A_slow_ftp_service_without_a_requested_load_is_disconnected()
    {
        var service = new CompanionStatusService(_options, new HangingFileSystem(), _llama, _sensors, new FakePayloadCatalog(), new FakeElfldrProbe(), _tracker, _time);

        var status = await service.RefreshAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(CompanionLifecycle.Disconnected, status.Lifecycle);
    }

    private sealed class HangingFileSystem : IPS5FileSystem
    {
        private static async Task<T> HangAsync<T>(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return default!;
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => HangAsync<bool>(cancellationToken);

        public Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) => HangAsync<bool>(cancellationToken);

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken) => HangAsync<IReadOnlyList<RemoteEntry>>(cancellationToken);

        public Task<IReadOnlyList<RemoteEntry>?> TryListAsync(string path, CancellationToken cancellationToken) => HangAsync<IReadOnlyList<RemoteEntry>?>(cancellationToken);

        public Task<string?> ReadTextAsync(string path, int maxBytes, CancellationToken cancellationToken) => HangAsync<string?>(cancellationToken);

        public Task<byte[]?> ReadBytesAsync(string path, int maxBytes, CancellationToken cancellationToken) => HangAsync<byte[]?>(cancellationToken);

        public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken) => HangAsync<bool>(cancellationToken);

        public Task UploadAsync(Stream content, string path, IProgress<long>? progress, CancellationToken cancellationToken) => HangAsync<bool>(cancellationToken);

        public Task MoveAsync(string from, string to, CancellationToken cancellationToken) => HangAsync<bool>(cancellationToken);

        public Task MoveDirectoryAsync(string from, string to, CancellationToken cancellationToken) => HangAsync<bool>(cancellationToken);

        public Task DeleteAsync(string path, CancellationToken cancellationToken) => HangAsync<bool>(cancellationToken);
    }

    private sealed class BlockingCatalog : IPayloadCatalog
    {
        private int _reads;

        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads => Volatile.Read(ref _reads);

        public async Task<PayloadCatalogSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            FirstStarted.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return PayloadCatalogSnapshot.NotChecked;
        }
    }

    [Fact]
    public async Task Before_a_model_is_loaded_the_status_comes_from_ftp_alone()
    {
        var status = await Create(ConsoleFixtures.PS5LMInstalled()).RefreshAsync(CancellationToken.None);

        Assert.Equal(CompanionLifecycle.AppAvailableNoModel, status.Lifecycle);
        Assert.Equal(PS5Variant.PS5LM, status.Variant);
        Assert.True(status.Capabilities.Ftp);
        Assert.True(status.Capabilities.ModelControl);
        Assert.True(status.Capabilities.SettingsRead);
        Assert.False(status.Capabilities.Telemetry);
        Assert.Null(status.Telemetry);
    }

    [Fact]
    public async Task Unreachable_ftp_and_closed_llama_is_disconnected()
    {
        var fs = ConsoleFixtures.PS5LMInstalled();
        fs.Unreachable = true;

        var status = await Create(fs).RefreshAsync(CancellationToken.None);

        Assert.Equal(CompanionLifecycle.Disconnected, status.Lifecycle);
        Assert.False(status.Capabilities.Ftp);
        Assert.NotNull(status.Message);
    }

    [Fact]
    public async Task A_running_llama_server_wins_over_a_dead_ftp_service()
    {
        var fs = ConsoleFixtures.PS5LMInstalled();
        fs.Unreachable = true;
        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, FakeLlamaProbe.Metrics(10, 1), "model");

        var status = await Create(fs).RefreshAsync(CancellationToken.None);

        Assert.Equal(CompanionLifecycle.Ready, status.Lifecycle);
        Assert.False(status.Capabilities.Ftp);
        Assert.True(status.Capabilities.Telemetry);
        Assert.Equal("model", status.LoadedModel);
    }

    [Fact]
    public async Task A_load_request_reads_as_loading_until_the_grace_period_ends()
    {
        var service = Create(ConsoleFixtures.PS5LMInstalled());
        _tracker.MarkRequested("/data/PS5LM/models/a.gguf");

        var loading = await service.RefreshAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(_options.LoadGraceSeconds + 1));
        var expired = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(CompanionLifecycle.Loading, loading.Lifecycle);
        Assert.Equal(CompanionLifecycle.AppAvailableNoModel, expired.Lifecycle);
    }

    [Fact]
    public async Task A_503_from_llama_server_is_loading()
    {
        _llama.Result = new LlamaProbeResult(LlamaServerState.Loading, null, null);

        var status = await Create(ConsoleFixtures.PS5LMInstalled()).RefreshAsync(CancellationToken.None);

        Assert.Equal(CompanionLifecycle.Loading, status.Lifecycle);
    }

    [Fact]
    public async Task Serving_clears_the_load_request_and_tracks_generation()
    {
        var service = Create(ConsoleFixtures.PS5LMInstalled());
        _tracker.MarkRequested("m");
        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, FakeLlamaProbe.Metrics(10, 1), "m");
        var ready = await service.RefreshAsync(CancellationToken.None);

        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, FakeLlamaProbe.Metrics(40, 2, processing: 1), "m");
        _time.Advance(TimeSpan.FromSeconds(3));
        var generating = await service.RefreshAsync(CancellationToken.None);

        Assert.Equal(CompanionLifecycle.Ready, ready.Lifecycle);
        Assert.False(_tracker.IsLoading(TimeSpan.FromHours(1)));
        Assert.Equal(CompanionLifecycle.Generating, generating.Lifecycle);
        Assert.Equal(30, generating.Telemetry!.GenerationTokensPerSecond, 3);
    }

    [Fact]
    public async Task A_pending_command_file_is_surfaced()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/load", "/data/PS5LM/models/a.gguf");

        var status = await Create(fs).RefreshAsync(CancellationToken.None);

        Assert.True(status.CommandPending);
        Assert.Contains("not picked it up", status.Message);
    }

    [Fact]
    public async Task Sensors_degrade_independently()
    {
        _sensors.Sensors = new ConsoleSensors(60, 55, 22, 0.3, []);

        var status = await Create(ConsoleFixtures.PS5LMInstalled()).RefreshAsync(CancellationToken.None);

        Assert.True(status.Capabilities.Sensors);
        Assert.Equal(60, status.Sensors!.SocTemperatureC);
    }

    [Fact]
    public async Task An_ambiguous_install_explains_itself_and_disables_control()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddDirectory("/data/homebrew/PPSA99581");

        var status = await Create(fs).RefreshAsync(CancellationToken.None);

        Assert.Equal(PS5Variant.Unknown, status.Variant);
        Assert.False(status.Capabilities.ModelControl);
        Assert.Contains("PS5:Variant", status.Message);
    }

    [Fact]
    public async Task The_installation_lookup_is_cached_between_refreshes()
    {
        var fs = ConsoleFixtures.PS5LMInstalled();
        var service = Create(fs);
        await service.RefreshAsync(CancellationToken.None);

        fs.Unreachable = true;
        _time.Advance(TimeSpan.FromSeconds(1));
        var status = await service.RefreshAsync(CancellationToken.None);

        // The cached installation is dropped when FTP fails, and the status turns disconnected.
        Assert.Equal(CompanionLifecycle.Disconnected, status.Lifecycle);
        Assert.Null(service.LastInstallation);
    }
}

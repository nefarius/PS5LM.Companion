using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
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

using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class PayloadDependencyTests
{
    private static readonly InstalledPayload EtaHen = new(PayloadDependencies.EtaHen, "repo/etahen@abc", "etaHEN");
    private static readonly InstalledPayload Kstuff = new(PayloadDependencies.KstuffLite, "repo/kstuff-lite@abcdef12", "kstuff-lite");

    [Fact]
    public void The_manager_address_defaults_to_port_8084_and_an_override_is_kept()
    {
        Assert.Equal("http://192.168.30.187:8084/", new PS5Options { Host = "192.168.30.187" }.PayloadManagerBaseAddress().AbsoluteUri);
        Assert.Equal("https://mgr.example:9/base/", new PS5Options { Host = "ps5", PayloadManagerUrl = "https://mgr.example:9/base" }.PayloadManagerBaseAddress().AbsoluteUri);
    }

    [Fact]
    public void An_unchecked_catalog_does_not_invent_installation_or_health()
    {
        var rows = PayloadDependencies.Evaluate(PayloadCatalogSnapshot.NotChecked, false, false, false, 9021);

        Assert.All(rows, row => Assert.Equal("Not checked yet.", row.Message));
        Assert.All(rows, row => Assert.False(row.CanLaunch));
        Assert.Equal(DependencyPresence.Bundled, rows.Single(row => row.Id == PayloadDependencies.Exporter).Presence);
    }

    [Fact]
    public void A_missing_manager_leaves_unprobeable_payloads_unavailable_and_open_ports_verified()
    {
        var rows = PayloadDependencies.Evaluate(PayloadCatalogSnapshot.Unreachable("down"), true, true, true, 9021);

        Assert.Equal(DependencyHealth.Unavailable, Row(rows, PayloadDependencies.KstuffLite).Health);
        Assert.Equal(DependencyPresence.Unknown, Row(rows, PayloadDependencies.EtaHen).Presence);
        Assert.Equal(DependencyHealth.Verified, Row(rows, PayloadDependencies.FtpSrv).Health);
        Assert.Equal(DependencyHealth.Verified, Row(rows, PayloadDependencies.Exporter).Health);
        Assert.Equal(DependencyHealth.Verified, Row(rows, PayloadDependencies.Elfldr).Health);
        Assert.False(Row(rows, PayloadDependencies.Elfldr).CanLaunch);
    }

    [Fact]
    public void A_missing_payload_is_not_launchable()
    {
        var rows = PayloadDependencies.Evaluate(Available(), false, false, false, 9021);

        var eta = Row(rows, PayloadDependencies.EtaHen);
        Assert.Equal(DependencyPresence.NotInstalled, eta.Presence);
        Assert.Contains("not installed", eta.Message, StringComparison.Ordinal);
        Assert.False(eta.CanLaunch);
    }

    [Fact]
    public void A_recorded_launch_stays_unverified_when_nothing_can_probe_the_payload()
    {
        var recorded = new PayloadCatalogSnapshot(true, true, null, [Kstuff], new HashSet<string> { Kstuff.Slot });

        var row = Row(PayloadDependencies.Evaluate(recorded, false, false, false, 9021), PayloadDependencies.KstuffLite);

        Assert.Equal(DependencyHealth.LaunchRecorded, row.Health);
        Assert.Contains("still active", row.Message, StringComparison.Ordinal);
        Assert.True(row.CanLaunch);
    }

    [Fact]
    public void A_running_process_verifies_a_payload_even_outside_payload_manager()
    {
        var catalog = new PayloadCatalogSnapshot(true, true, null, [], new HashSet<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "KStuff.elf", "shadowmountplus.elf" });

        var rows = PayloadDependencies.Evaluate(catalog, false, false, false, 9021);

        var kstuff = Row(rows, PayloadDependencies.KstuffLite);
        Assert.Equal(DependencyHealth.Verified, kstuff.Health);
        Assert.Equal(DependencyPresence.Installed, kstuff.Presence);
        Assert.Contains("outside Payload Manager", kstuff.Message, StringComparison.Ordinal);
        Assert.Equal(DependencyHealth.Verified, Row(rows, PayloadDependencies.ShadowMountPlus).Health);
        Assert.Equal(DependencyPresence.NotInstalled, Row(rows, PayloadDependencies.EtaHen).Presence);
    }

    [Fact]
    public void A_service_answering_on_its_port_is_present_even_when_payload_manager_does_not_list_it()
    {
        var rows = PayloadDependencies.Evaluate(Available(), true, false, true, 9021);

        Assert.Equal(DependencyPresence.Installed, Row(rows, PayloadDependencies.FtpSrv).Presence);
        Assert.Equal(DependencyPresence.Installed, Row(rows, PayloadDependencies.Elfldr).Presence);
    }

    [Fact]
    public void An_installed_payload_without_a_process_is_not_running_when_the_list_was_read()
    {
        var catalog = new PayloadCatalogSnapshot(true, true, null, [Kstuff], new HashSet<string> { Kstuff.Slot }, new HashSet<string>());

        var row = Row(PayloadDependencies.Evaluate(catalog, false, false, false, 9021), PayloadDependencies.KstuffLite);

        Assert.Equal(DependencyHealth.NotRunning, row.Health);
        Assert.Contains("kstuff.elf", row.Message, StringComparison.Ordinal);
        Assert.True(row.CanLaunch);
    }

    [Fact]
    public void Port_checks_outrank_a_recorded_launch_and_elfldr_is_never_offered()
    {
        var recorded = new PayloadCatalogSnapshot(
            true,
            true,
            null,
            [new InstalledPayload(PayloadDependencies.FtpSrv, "repo/ftpsrv@1", "ftpsrv"), new InstalledPayload(PayloadDependencies.Elfldr, "repo/elfldr@1", "elfldr")],
            new HashSet<string> { "repo/ftpsrv@1", "repo/elfldr@1" });

        var rows = PayloadDependencies.Evaluate(recorded, false, false, false, 9021);

        Assert.Equal(DependencyHealth.Unknown, Row(rows, PayloadDependencies.FtpSrv).Health);
        Assert.Contains("not answering", Row(rows, PayloadDependencies.FtpSrv).Message, StringComparison.Ordinal);
        var elfldr = Row(rows, PayloadDependencies.Elfldr);
        Assert.False(elfldr.CanLaunch);
        Assert.Contains("cannot start elfldr itself", elfldr.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_status_service_publishes_verified_ports_and_the_manager_failure()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
        var catalog = new FakePayloadCatalog { Snapshot = PayloadCatalogSnapshot.Unreachable("down") };
        var service = new CompanionStatusService(
            new PS5Options { Host = "ps5" },
            ConsoleFixtures.PS5LMInstalled(),
            new FakeLlamaProbe(),
            new FakeSensorProbe { Sensors = new ConsoleSensors(1, 1, 1, 1, []) },
            catalog,
            new FakeElfldrProbe { Listening = true },
            new LoadTracker(time),
            time);

        var status = await service.RefreshAsync(CancellationToken.None);

        Assert.False(status.PayloadManagerAvailable);
        Assert.Equal("down", status.PayloadManagerMessage);
        Assert.Equal(DependencyHealth.Verified, Row(status.Dependencies, PayloadDependencies.FtpSrv).Health);
        Assert.Equal(DependencyHealth.Verified, Row(status.Dependencies, PayloadDependencies.Exporter).Health);
        Assert.Equal(DependencyHealth.Verified, Row(status.Dependencies, PayloadDependencies.Elfldr).Health);
    }

    [Fact]
    public async Task Launch_uses_the_installed_slot_and_refreshes_status()
    {
        var (service, launcher, store) = Harness(Available(EtaHen), listening: true);

        var result = await service.LaunchAsync(PayloadDependencies.EtaHen, CancellationToken.None);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Contains("hand-off", result.Message, StringComparison.Ordinal);
        Assert.Equal(["repo/etahen@abc"], launcher.Launched);
        Assert.True(store.Current.PayloadManagerAvailable);
    }

    [Fact]
    public async Task A_second_launch_is_refused_while_the_first_is_running()
    {
        var launcher = new FakePayloadLauncher
        {
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var service = Harness(Available(EtaHen), listening: true, launcher).Service;

        var first = service.LaunchAsync(PayloadDependencies.EtaHen, CancellationToken.None);
        await launcher.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await service.LaunchAsync(PayloadDependencies.EtaHen, CancellationToken.None);
        launcher.Release!.TrySetResult();
        var done = await first.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(OperationOutcome.Conflict, second.Outcome);
        Assert.Equal(OperationOutcome.Accepted, done.Outcome);
        Assert.Equal(["repo/etahen@abc"], launcher.Launched);
    }

    [Theory]
    [InlineData(PayloadDependencies.Elfldr)]
    [InlineData(PayloadDependencies.Exporter)]
    [InlineData(PayloadDependencies.ManagerId)]
    public async Task Elfldr_the_bundled_exporter_and_the_manager_itself_are_refused(string id)
    {
        var (service, launcher, _) = Harness(Available(EtaHen), listening: true);

        var result = await service.LaunchAsync(id, CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, result.Outcome);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task A_launch_without_elfldr_explains_that_the_manager_cannot_bootstrap_it()
    {
        var (service, launcher, _) = Harness(Available(EtaHen), listening: false);

        var result = await service.LaunchAsync(PayloadDependencies.EtaHen, CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("cannot start elfldr itself", result.Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task A_missing_slot_and_a_down_manager_do_not_launch()
    {
        var missing = Harness(Available(), listening: true);
        var down = Harness(PayloadCatalogSnapshot.Unreachable("down"), listening: true);

        var missingResult = await missing.Service.LaunchAsync(PayloadDependencies.EtaHen, CancellationToken.None);
        var downResult = await down.Service.LaunchAsync(PayloadDependencies.EtaHen, CancellationToken.None);

        Assert.Equal(OperationOutcome.NotFound, missingResult.Outcome);
        Assert.Equal(OperationOutcome.Unavailable, downResult.Outcome);
        Assert.Empty(missing.Launcher.Launched);
        Assert.Empty(down.Launcher.Launched);
    }

    [Fact]
    public async Task An_unsafe_slot_is_refused()
    {
        var (service, launcher, _) = Harness(Available(new InstalledPayload(PayloadDependencies.EtaHen, "../etahen", "etaHEN")), listening: true);

        var result = await service.LaunchAsync(PayloadDependencies.EtaHen, CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, result.Outcome);
        Assert.Empty(launcher.Launched);
    }

    private static PayloadCatalogSnapshot Available(params InstalledPayload[] installed) =>
        new(true, true, null, installed, new HashSet<string>());

    private static DependencyStatus Row(IReadOnlyList<DependencyStatus> rows, string id) => rows.Single(row => row.Id == id);

    private static (DependencyLaunchService Service, FakePayloadLauncher Launcher, CompanionStatusStore Store) Harness(
        PayloadCatalogSnapshot snapshot,
        bool listening,
        FakePayloadLauncher? launcher = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero));
        var options = new PS5Options { Host = "ps5", ElfldrPort = 9021 };
        var catalog = new FakePayloadCatalog { Snapshot = snapshot };
        var elfldr = new FakeElfldrProbe { Listening = listening };
        launcher ??= new FakePayloadLauncher();
        var status = new CompanionStatusService(
            options,
            ConsoleFixtures.PS5LMInstalled(),
            new FakeLlamaProbe(),
            new FakeSensorProbe(),
            catalog,
            elfldr,
            new LoadTracker(time),
            time);
        var store = new CompanionStatusStore();
        return (new DependencyLaunchService(options, catalog, launcher, elfldr, status, store), launcher, store);
    }
}

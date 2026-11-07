using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Core.Updates;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ComponentUpdateServiceTests
{
    [Fact]
    public async Task Check_only_never_uploads_and_republishes_structured_updates()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(harness.Stored.Single(stored => stored.FileName == "kstuff-lite.elf").Path!, "old build");
        var store = new CompanionStatusStore();
        store.Publish(CompanionStatus.Initial with { UpdatedAt = DateTimeOffset.UtcNow });
        var service = Service(harness, store);

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Empty(harness.Events);
        var update = Assert.Single(status.Outdated);
        Assert.Equal("kstuff-lite", update.Id);
        Assert.False(status.Checking);
        Assert.NotNull(status.HashCheckedAt);
        Assert.Equal(["kstuff-lite"], store.Current.Updates!.Outdated.Select(item => item.Id).ToArray());
    }

    [Fact]
    public async Task A_fail_line_becomes_the_snapshot_failure()
    {
        using var harness = new SetupHarness();
        harness.Ports.Remove(8084);
        var store = new CompanionStatusStore();
        store.Publish(CompanionStatus.Initial);
        var service = Service(harness, store);

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.NotNull(status.Failure);
        Assert.Contains("run setup", status.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(status.Outdated);
        Assert.Equal(status.Failure, store.Current.Updates!.Failure);
    }

    [Fact]
    public async Task A_failed_check_keeps_the_previous_updates()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(harness.Stored.Single(stored => stored.FileName == "kstuff-lite.elf").Path!, "old build");
        var store = new CompanionStatusStore();
        store.Publish(CompanionStatus.Initial with { UpdatedAt = DateTimeOffset.UtcNow });
        var service = Service(harness, store);
        await service.CheckAsync(CancellationToken.None);
        harness.Ports.Remove(8084);

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(["kstuff-lite"], status.Outdated.Select(item => item.Id).ToArray());
        Assert.NotNull(status.Failure);
        Assert.False(status.Checking);
    }

    [Fact]
    public async Task A_current_console_clears_previous_updates()
    {
        using var harness = await PreparedAsync();
        var store = new CompanionStatusStore();
        store.Publish(CompanionStatus.Initial with
        {
            Updates = new ComponentUpdateStatus([new ComponentUpdate("kstuff-lite", "kstuff-lite", "v1.11", "stale")], DateTimeOffset.UnixEpoch, false, null),
        });
        var service = Service(harness, store);

        var status = await service.CheckAsync(CancellationToken.None);

        Assert.Empty(status.Outdated);
        Assert.Empty(status.Settings);
        Assert.Empty(store.Current.Updates!.Outdated);
    }

    [Fact]
    public async Task A_short_countdown_is_published_as_settings_drift()
    {
        using var harness = await PreparedAsync();
        harness.Autoload = harness.Autoload with { DelaySeconds = 3, DelayReported = true };
        var store = new CompanionStatusStore();
        store.Publish(CompanionStatus.Initial with { UpdatedAt = DateTimeOffset.UtcNow });

        var status = await Service(harness, store).CheckAsync(CancellationToken.None);

        Assert.Equal("autoload-delay", Assert.Single(status.Settings).Id);
        Assert.Equal(3, harness.Autoload.DelaySeconds);
        Assert.Equal("autoload-delay", Assert.Single(store.Current.Updates!.Settings).Id);
    }

    [Fact]
    public async Task Fix_settings_leaves_bundled_kstuff_when_kstuff_lite_is_not_stored()
    {
        using var harness = await PreparedAsync();
        harness.Stored.RemoveAll(item => item.FileName == "kstuff-lite.elf");
        var config = harness.Files.Text("/data/OnionHEN/config.ini")!.Replace("autoload=false", "autoload=true", StringComparison.Ordinal);
        harness.Files.AddFile("/data/OnionHEN/config.ini", config);
        await harness.Files.DeleteAsync("/data/OnionHEN/no_kstuff", CancellationToken.None);
        var store = new CompanionStatusStore();
        store.Publish(CompanionStatus.Initial with { UpdatedAt = DateTimeOffset.UtcNow });

        await Service(harness, store).ApplySettingsAsync(CancellationToken.None);

        Assert.Equal(config, harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Null(harness.Files.Text("/data/OnionHEN/no_kstuff"));
        Assert.DoesNotContain(store.Current.Updates!.Settings, item => item.Id == "onionhen-kstuff");
    }

    private static async Task<SetupHarness> PreparedAsync()
    {
        var harness = new SetupHarness();
        harness.VoidShell.SetUp([SetupHarness.TitleId]);
        harness.Files.AddFile("/data/OnionHEN/config.ini", "[app_jailbreak]\nexact_title_ids = PPSA00001\n");
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        foreach (var payload in harness.Manifest.Payloads)
        {
            var local = await harness.Artifacts.FetchAsync(payload.Url, payload.Sha256, CancellationToken.None);
            var stored = harness.Stored.Single(item => item.FileName == payload.UploadName);
            harness.Files.AddFile(stored.Path!, await File.ReadAllBytesAsync(local));
        }

        harness.Events.Clear();
        return harness;
    }

    private static ComponentUpdateService Service(SetupHarness harness, CompanionStatusStore store) => new(
        harness.Manifest,
        new PS5Options { Host = "ps5.test" },
        new SetupServices(harness.Manager, harness.Artifacts, harness.Files, harness.Probe, harness.Elfldr, harness.VoidShell, new SetupHarness.FakeUi(harness), TimeProvider.System),
        store,
        TimeProvider.System);
}

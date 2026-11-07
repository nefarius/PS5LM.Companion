using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class BootSettingsTests
{
    private const string ConfigPath = "/data/OnionHEN/config.ini";

    [Fact]
    public async Task A_prepared_console_matches_the_recommendation()
    {
        using var harness = await PreparedAsync();

        var outcome = await Check(harness);

        Assert.Empty(outcome.Drift);
        Assert.Contains(outcome.Lines, line => line.Status == SetupStatus.Ok && line.Message.Contains("match the recommendation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reordered_list_is_drift_and_a_foreign_entry_is_not()
    {
        using var harness = await PreparedAsync();
        harness.Autoload = harness.Autoload with
        {
            List = harness.Autoload.List.Replace(
                "local/elfldr,!6000,local/kstuff-lite,!8000",
                "local/kstuff-lite,!8000,local/elfldr,!6000",
                StringComparison.Ordinal),
        };

        var reordered = await Check(harness);

        Assert.Equal("autoload-order", Assert.Single(reordered.Drift).Id);

        harness.Autoload = new AutoloadConfig(true, harness.Autoload.List + ",!3000,local/mine", harness.Autoload.DelaySeconds, true);
        var withForeign = await Check(harness);

        Assert.Equal(["autoload-order"], withForeign.Drift.Select(item => item.Id).ToArray());
        Assert.DoesNotContain(withForeign.Drift, item => item.Detail.Contains("mine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_short_pause_a_duplicate_and_a_low_countdown_are_separate_findings()
    {
        using var harness = await PreparedAsync();
        harness.Autoload = new AutoloadConfig(
            true,
            harness.Autoload.List.Replace("!8000", "!1000", StringComparison.Ordinal) + ",!3000,local/kstuff",
            3,
            true);

        var outcome = await Check(harness);

        Assert.Contains(outcome.Drift, item => item.Id == "autoload-pause" && item.Detail.Contains("1000 ms", StringComparison.Ordinal));
        Assert.Contains(outcome.Drift, item => item.Id == "autoload-duplicate" && item.Detail.Contains("kstuff", StringComparison.Ordinal));
        Assert.Contains(outcome.Drift, item => item.Id == "autoload-delay" && item.Recommended == "10 s");
    }

    [Fact]
    public async Task An_unreported_countdown_is_not_drift()
    {
        using var harness = await PreparedAsync();
        harness.Autoload = harness.Autoload with { DelaySeconds = 0, DelayReported = false };

        var outcome = await Check(harness);

        Assert.DoesNotContain(outcome.Drift, item => item.Id == "autoload-delay");
    }

    [Fact]
    public async Task Bundled_kstuff_left_on_is_drift()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(ConfigPath, harness.Files.Text(ConfigPath)!.Replace("autoload=false", "autoload=true", StringComparison.Ordinal));

        var outcome = await Check(harness);

        Assert.Equal("onionhen-kstuff", Assert.Single(outcome.Drift).Id);
    }

    [Fact]
    public async Task A_missing_config_is_not_confirmed_and_is_not_drift()
    {
        using var harness = await PreparedAsync();
        await harness.Files.DeleteAsync(ConfigPath, CancellationToken.None);

        var outcome = await Check(harness);

        Assert.DoesNotContain(outcome.Drift, item => item.Id == "onionhen-kstuff");
        Assert.Contains(outcome.Lines, line => line.Status == SetupStatus.Warn && line.Message.Contains("not confirmed off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unprepared_console_has_no_findings()
    {
        using var harness = new SetupHarness();
        harness.Autoload = new AutoloadConfig(false, "local/something", 1, true);

        var outcome = await Check(harness);

        Assert.Empty(outcome.Drift);
        Assert.Empty(outcome.Lines);
    }

    [Fact]
    public async Task An_older_payload_manager_is_not_flagged_for_the_countdown()
    {
        using var harness = await PreparedAsync();
        harness.Version = "0.5.1";
        harness.Autoload = new AutoloadConfig(false, "local/mine", 1, true);

        var outcome = await Check(harness);

        Assert.DoesNotContain(outcome.Drift, item => item.Id.StartsWith("autoload", StringComparison.Ordinal));
        Assert.Contains(outcome.Lines, line => line.Message.Contains("older than 0.6.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_older_onionhen_is_not_flagged_and_apply_does_not_edit_it()
    {
        using var harness = await PreparedAsync();
        var onion = harness.Stored.Single(item => item.FileName == "onionhen.elf");
        harness.Stored.Remove(onion);
        harness.Stored.Add(new ManagedPayload("repo/onionhen@v0.0.13", "onionhen", onion.FileName, onion.Path));
        var config = "[app_jailbreak]\nexact_title_ids = PPSA00001\n";
        harness.Files.AddFile(ConfigPath, config);
        await harness.Files.DeleteAsync("/data/OnionHEN/no_kstuff", CancellationToken.None);

        var outcome = await Check(harness);

        Assert.DoesNotContain(outcome.Drift, item => item.Id == "onionhen-kstuff");
        Assert.Contains(outcome.Lines, line => line.Message.Contains("v0.0.13", StringComparison.Ordinal));

        var lines = new List<string>();
        var resolved = Resolved(harness);
        await BootSettings.ApplyKstuffAsync(harness.Manifest, resolved, Services(harness), 2121, new OnionConfigSession(), (_, _, message) => lines.Add(message), CancellationToken.None);

        Assert.Equal(config, harness.Files.Text(ConfigPath));
        Assert.Null(harness.Files.Text("/data/OnionHEN/no_kstuff"));
        Assert.Contains(lines, line => line.Contains("left unchanged", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_is_idempotent_and_keeps_a_foreign_entry()
    {
        using var harness = await PreparedAsync();
        harness.Autoload = new AutoloadConfig(true, harness.Autoload.List.Replace("!8000", "!1000", StringComparison.Ordinal) + ",!3000,local/mine", 4, true);
        var lines = new List<(string Component, SetupStatus Status, string Message)>();
        var resolved = Resolved(harness);

        await BootSettings.ApplyAutoloadAsync(resolved, Services(harness), (component, status, message) => lines.Add((component, status, message)), CancellationToken.None);
        var list = harness.Autoload.List;
        var bak = harness.Files.Text("/data/OnionHEN/config.ini.bak");
        await BootSettings.ApplyAutoloadAsync(resolved, Services(harness), (component, status, message) => lines.Add((component, status, message)), CancellationToken.None);
        await BootSettings.ApplyKstuffAsync(harness.Manifest, resolved, Services(harness), 2121, new OnionConfigSession(), (_, _, _) => { }, CancellationToken.None);

        Assert.Contains("local/mine", list, StringComparison.Ordinal);
        Assert.Contains("!8000", list, StringComparison.Ordinal);
        Assert.Equal(SetupRunner.MinimumAutoloadDelaySeconds, harness.Autoload.DelaySeconds);
        Assert.Equal(list, harness.Autoload.List);
        Assert.Equal(bak, harness.Files.Text("/data/OnionHEN/config.ini.bak"));
        Assert.Contains("[kstuff]\nautoload=false\n", harness.Files.Text(ConfigPath));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void A_transfer_or_a_model_load_blocks_a_fix(bool transfer, bool loading, bool command)
    {
        var status = CompanionStatus.Initial with
        {
            Lifecycle = loading ? CompanionLifecycle.Loading : CompanionLifecycle.Ready,
            CommandPending = command,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Assert.Equal(transfer || loading || command, BootSettings.IsBusy(status, transfer));
    }

    private static async Task<SetupHarness> PreparedAsync()
    {
        var harness = new SetupHarness();
        harness.VoidShell.SetUp([SetupHarness.TitleId]);
        harness.Files.AddFile(ConfigPath, "[general]\nx=1\n[app_jailbreak]\nexact_title_ids = PPSA00001\n");
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        harness.Events.Clear();
        return harness;
    }

    private static Task<BootSettingsOutcome> Check(SetupHarness harness) =>
        BootSettings.CheckAsync(harness.Manifest, Services(harness), 2121, CancellationToken.None);

    private static SetupServices Services(SetupHarness harness) => new(
        harness.Manager,
        harness.Artifacts,
        harness.Files,
        harness.Probe,
        harness.Elfldr,
        harness.VoidShell,
        new SetupHarness.FakeUi(harness),
        TimeProvider.System);

    private static List<(ManifestPayload Payload, ManagedPayload Stored)> Resolved(SetupHarness harness)
    {
        var resolved = new List<(ManifestPayload, ManagedPayload)>();
        foreach (var payload in harness.Manifest.Payloads)
        {
            if (SetupRunner.FindStored(harness.Stored, payload) is { } stored)
            {
                resolved.Add((payload, stored));
            }
        }

        return resolved;
    }
}

using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class SetupRunnerTests
{
    private const string OnionConfig = "[general]\nx=1\n[app_jailbreak]\nexact_title_ids = PPSA00001\n";

    private static SetupHarness Ready()
    {
        var harness = new SetupHarness();
        harness.VoidShell.SetUp([SetupHarness.TitleId]);
        harness.Files.AddFile("/data/OnionHEN/config.ini", OnionConfig);
        return harness;
    }

    [Fact]
    public async Task Fresh_console_gets_payloads_autoload_extras_allow_list_and_app()
    {
        using var harness = Ready();

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(
            "local/elfldr,local/kstuff-lite,!3000,local/onionhen,!3000,local/ftpsrv,local/shadowmountplus,local/voidshell,local/ps5-web-file-manager",
            harness.Autoload.List);
        Assert.True(harness.Autoload.Enabled);
        Assert.Equal(
            ["elfldr", "kstuff-lite", "onionhen", "ftpsrv", "shadowmountplus", "voidshell", "ps5-web-file-manager"],
            harness.Events.Where(e => e.StartsWith("load:", StringComparison.Ordinal)).Select(e => e[5..^4]).ToArray());
        Assert.Equal("<html>dashboard</html>", harness.Files.Text("/data/voidshell/www/index.html"));
        Assert.DoesNotContain("/data/voidshell/www/index.html.part", harness.Files.Files.Keys);
        Assert.Contains("PPSA00001,PPSA99581", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Equal(OnionConfig, harness.Files.Text("/data/OnionHEN/config.ini.bak"));
        Assert.Equal(SetupHarness.ParamV1, harness.Files.Text("/data/homebrew/PPSA99581/sce_sys/param.json"));
        Assert.Equal("PNG", harness.Files.Text("/data/homebrew/PPSA99581/assets/logo.png"));
        Assert.DoesNotContain(harness.Files.Files.Keys, key => key.StartsWith("/data/ps5lm-companion/staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Second_run_uploads_and_launches_nothing_new()
    {
        using var harness = Ready();
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        harness.Events.Clear();
        harness.Files.AddFile("/data/OnionHEN/config.ini.bak", "stale");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Empty(harness.Events);
        Assert.Equal("stale", harness.Files.Text("/data/OnionHEN/config.ini.bak"));
    }

    [Fact]
    public async Task Existing_foreign_autoload_entries_are_kept_after_ours()
    {
        using var harness = Ready();
        harness.Autoload = new(false, "local/mine,!500");

        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.EndsWith(",local/ps5-web-file-manager,local/mine", harness.Autoload.List);
    }

    [Fact]
    public async Task Optional_voidshell_that_does_not_start_is_only_a_warning()
    {
        using var harness = Ready();
        harness.LoadFails.Add("voidshell.elf");
        harness.Manifest = harness.Manifest with
        {
            Payloads = harness.Manifest.Payloads
                .Select(p => p.Id == "voidshell"
                    ? p with { Optional = true, OptionalNote = "needs sceAppInstUtilAppInstallTitleDir on this firmware" }
                    : p)
                .ToList(),
        };

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        var line = Assert.Single(report.Lines, l => l.Component == "VoidShell");
        Assert.Equal(SetupStatus.Warn, line.Status);
        Assert.Contains("sceAppInstUtilAppInstallTitleDir", line.Message);
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task App_archive_for_another_title_is_rejected_before_anything_is_written()
    {
        using var harness = Ready();

        // The fake archive carries PPSA99581; asking for PS5LM (PPSA99575) must not install it.
        var report = await harness.Runner(SetupHarness.Options("ps5lm")).RunAsync(CancellationToken.None);

        Assert.Contains(report.Lines, l => l.Component == "PS5LM" && l.Status == SetupStatus.Fail && l.Message.Contains("PPSA99581", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Files.Files.Keys, key => key.Contains("PPSA99575", StringComparison.Ordinal) || key.Contains("PPSA99581", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Title_is_found_in_the_shadowmount_log_when_voidshell_lists_no_games()
    {
        using var harness = Ready();
        harness.VoidShell.SetUp([]);
        harness.Files.AddFile("/data/shadowmount/debug.log", "NOTIFY: installed game PPSA99581\n");
        harness.Files.AfterMove = () => harness.Files.AddFile(
            "/data/shadowmount/debug.log",
            harness.Files.Text("/data/shadowmount/debug.log") + "[WAIT] PS5LM PPSA99581\nNOTIFY: installed game PPSA99581\n");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus install" && l.Status == SetupStatus.Ok);
    }

    [Fact]
    public async Task Stale_install_log_line_is_not_taken_as_proof_for_a_new_install()
    {
        using var harness = Ready();
        harness.VoidShell.SetUp([]);
        harness.Files.AddFile("/data/shadowmount/debug.log", "NOTIFY: installed game PPSA99581\n");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus install" && l.Status == SetupStatus.Warn);
    }

    [Fact]
    public async Task Unchanged_app_is_confirmed_through_voidshell_not_the_log()
    {
        using var harness = Ready();
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        harness.Files.AddFile("/data/shadowmount/debug.log", "NOTIFY: installed game PPSA99581\n");
        harness.VoidShell.SetUp([]);

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus install" && l.Status == SetupStatus.Warn);
    }

    [Fact]
    public async Task Voidshell_is_asked_to_rescan_after_the_title_registers()
    {
        using var harness = Ready();

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains("7007/api/rescan", harness.Probe.Posts);
        Assert.Contains(report.Lines, l => l.Component == "VoidShell library" && l.Status == SetupStatus.Ok && l.Message.Contains("library rescan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_ftp_is_a_failure_and_blocks_extras_and_app()
    {
        using var harness = Ready();
        harness.LoadFails.Add("ftpsrv.elf");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Fail);
        Assert.Contains(report.Lines, l => l.Component == "PS5LM Plus" && l.Status == SetupStatus.Fail);
        Assert.Null(harness.Files.Text("/data/voidshell/www/index.html"));
    }

    [Fact]
    public async Task Failed_elfldr_stops_launching_the_rest()
    {
        using var harness = Ready();
        harness.LoadFails.Add("elfldr.elf");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.DoesNotContain(harness.Events, e => e.StartsWith("load:", StringComparison.Ordinal));
        Assert.Contains(report.Lines, l => l.Component == "elfldr" && l.Message.Contains("9021", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Extra_file_is_skipped_when_the_size_matches_and_resent_with_force()
    {
        using var harness = Ready();
        harness.Files.AddFile("/data/voidshell/www/index.html", SetupHarness.FakeStore.DashboardBytes);
        var before = harness.Files.Files["/data/voidshell/www/index.html"];

        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        Assert.Same(before, harness.Files.Files["/data/voidshell/www/index.html"]);

        await harness.Runner(SetupHarness.Options() with { Force = true }).RunAsync(CancellationToken.None);
        Assert.NotSame(before, harness.Files.Files["/data/voidshell/www/index.html"]);
    }

    [Fact]
    public async Task Different_installed_version_is_overwritten_in_place_and_models_survive()
    {
        using var harness = Ready();
        harness.Files.AddFile("/data/homebrew/PPSA99581/sce_sys/param.json", "PARAM-old");
        harness.Files.AddFile("/data/homebrew/PPSA99581/models/keep.gguf", "model");

        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(SetupHarness.ParamV1, harness.Files.Text("/data/homebrew/PPSA99581/sce_sys/param.json"));
        Assert.Equal("model", harness.Files.Text("/data/homebrew/PPSA99581/models/keep.gguf"));
        Assert.DoesNotContain(harness.Files.Files.Keys, key => key.StartsWith("/data/ps5lm-companion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Skip_app_leaves_homebrew_and_onionhen_alone()
    {
        using var harness = Ready();

        var report = await harness.Runner(SetupHarness.Options(app: null) with { SkipApp = true }).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(OnionConfig, harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.DoesNotContain(harness.Files.Files.Keys, key => key.StartsWith("/data/homebrew", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_app_id_fails_before_touching_the_console()
    {
        using var harness = Ready();

        var report = await harness.Runner(SetupHarness.Options("nope")).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.Empty(harness.Events);
    }

    [Fact]
    public async Task Unreachable_manager_is_reported_without_other_work()
    {
        using var harness = Ready();
        harness.Ports.Clear();

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.Single(report.Lines);
    }

    [Fact]
    public async Task Old_manager_without_consent_stops_with_the_flag_named()
    {
        using var harness = Ready();
        harness.Version = "0.5.2";

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.Contains("--upgrade-payload-manager", report.Lines.Single().Message, StringComparison.Ordinal);
        Assert.Empty(harness.Events);
    }

    [Fact]
    public async Task Upgrade_stores_the_versioned_file_loads_elfldr_first_and_shuts_down_only_after_9021_answers()
    {
        using var harness = Ready();
        harness.Version = "0.5.2";
        harness.CanPrompt = true;
        harness.Consent = true;

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        var events = harness.Events;
        Assert.Equal("upload:pldmgr_v0.6.3.elf", events[0]);
        var elfldrLoad = events.IndexOf("load:elfldr.elf");
        var shutdown = events.IndexOf("shutdown");
        Assert.InRange(elfldrLoad, 1, shutdown - 1);
        Assert.Equal("elfldr-send:pldmgr_v0.6.3.elf", events[shutdown + 1]);
        Assert.Contains(report.Lines, l => l.Component == "Payload Manager" && l.Message.StartsWith("Upgraded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upgrade_never_shuts_down_the_manager_when_elfldr_cannot_be_started()
    {
        using var harness = Ready();
        harness.Version = "0.5.2";
        harness.LoadFails.Add("elfldr.elf");

        var report = await harness.Runner(SetupHarness.Options() with { UpgradePayloadManager = true }).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.DoesNotContain("shutdown", harness.Events);
        Assert.Contains("next time the console starts it", report.Lines.Last().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_handoff_after_shutdown_reports_the_reboot_fallback()
    {
        using var harness = Ready();
        harness.Version = "0.5.2";

        // elfldr is closed and loads fine, but the sender fails after the manager is gone.
        var failing = new SetupServices(
            harness.Manager,
            harness.Artifacts,
            harness.Files,
            harness.Probe,
            new ThrowingElfldr(),
            harness.VoidShell,
            new SetupHarness.FakeUi(harness),
            TimeProvider.System);
        var report = await new SetupRunner(harness.Manifest, SetupHarness.Options() with { UpgradePayloadManager = true }, failing).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.Contains("autoloader", report.Lines.Last().Message, StringComparison.Ordinal);
        Assert.Contains("shutdown", harness.Events);
    }

    [Fact]
    public async Task Verify_manifest_fetches_every_artifact()
    {
        using var harness = Ready();

        var ok = await SetupRunner.VerifyManifestAsync(harness.Manifest, harness.Artifacts, new SetupHarness.FakeUi(harness), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(harness.Manifest.Artifacts().Count(), harness.Artifacts.Fetched.Count);
    }

    private sealed class ThrowingElfldr : IElfldrSender
    {
        public Task SendAsync(string filePath, CancellationToken cancellationToken) => throw new IOException("connection reset");
    }
}

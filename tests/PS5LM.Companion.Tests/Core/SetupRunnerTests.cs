using System.Text;

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
            "local/elfldr,!6000,local/kstuff-lite,!8000,local/ftpsrv,!3000,local/voidshell,!3000,local/onionhen,!25000,local/shadowmountplus,!5000,local/ps5-web-file-manager",
            harness.Autoload.List);
        Assert.True(harness.Autoload.Enabled);
        Assert.Equal(SetupRunner.MinimumAutoloadDelaySeconds, harness.Autoload.DelaySeconds);
        Assert.Equal(
            ["elfldr", "ftpsrv", "kstuff-lite", "voidshell", "onionhen", "shadowmountplus", "ps5-web-file-manager"],
            harness.Events.Where(e => e.StartsWith("load:", StringComparison.Ordinal)).Select(e => e[5..^4]).ToArray());
        KstuffSafety.AssertBoot(harness, report.ExitCode);
        Assert.Equal("<html>dashboard</html>", harness.Files.Text("/data/voidshell/www/index.html"));
        Assert.DoesNotContain("/data/voidshell/www/index.html.part", harness.Files.Files.Keys);
        Assert.Contains("PPSA00001,PPSA99581", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Contains("[kstuff]\nautoload=false\n", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Equal(string.Empty, harness.Files.Text("/data/OnionHEN/no_kstuff"));
        Assert.Equal(OnionConfig, harness.Files.Text("/data/OnionHEN/config.ini.bak"));
        var kstuffCheck = harness.Messages.FindIndex(message => message.Contains("bundled kstuff", StringComparison.Ordinal));
        var onionLaunch = harness.Messages.FindIndex(message => message.Equals("Launching OnionHEN", StringComparison.Ordinal));
        Assert.True(kstuffCheck >= 0 && onionLaunch > kstuffCheck);
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
    public async Task Repository_entry_with_the_same_id_counts_as_already_stored()
    {
        using var harness = Ready();
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        harness.Stored.RemoveAll(s => s.FileName == "ftpsrv.elf");
        harness.Stored.Insert(0, new ManagedPayload(
            "repo/ftpsrv@e8cce5d1",
            "ftpsrv",
            "ftpsrv_v0.21.1.elf",
            "/data/pldmgr/payloads/repo/ftpsrv@e8cce5d1/ftpsrv_v0.21.1.elf"));
        harness.Events.Clear();

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.DoesNotContain("upload:ftpsrv.elf", harness.Events);
        Assert.Contains("repo/ftpsrv@e8cce5d1", harness.Autoload.List, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_replaces_a_repository_autoload_slot_for_a_payload_it_stores_locally()
    {
        using var harness = Ready();
        harness.Autoload = new(false, "repo/ftpsrv@e8cce5d1,local/mine,!500");

        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains("local/ftpsrv", harness.Autoload.List, StringComparison.Ordinal);
        Assert.DoesNotContain("repo/ftpsrv@", harness.Autoload.List, StringComparison.Ordinal);
        Assert.Contains("local/mine", harness.Autoload.List, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_foreign_autoload_entries_are_kept_after_ours()
    {
        using var harness = Ready();
        harness.Autoload = new(false, "local/mine,!500");

        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.EndsWith(",local/ps5-web-file-manager,!3000,local/mine", harness.Autoload.List);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Autoload_list_longer_than_payload_manager_accepts_is_not_saved(bool multibyte)
    {
        using var harness = Ready();
        var existing = multibyte
            ? MultibyteEntryOverTheByteLimit(harness)
            : "local/" + new string('x', AutoloadListBuilder.MaxListLength);
        harness.Autoload = new(false, existing);

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(existing, harness.Autoload.List);
        Assert.False(harness.Autoload.Enabled);
        var line = Assert.Single(report.Lines, l => l.Component == "Autoload" && l.Status == SetupStatus.Fail);
        Assert.Contains($"{AutoloadListBuilder.MaxListLength} bytes", line.Message, StringComparison.Ordinal);
        Assert.Contains("Shorten the existing autoload list", line.Message, StringComparison.Ordinal);
        Assert.Equal(2, report.ExitCode);
    }

    /// <summary>
    /// An existing entry whose finished list is still within <see cref="AutoloadListBuilder.MaxListLength"/> characters
    /// and past it in UTF-8 bytes. A character-count check would save this list.
    /// </summary>
    private static string MultibyteEntryOverTheByteLimit(SetupHarness harness)
    {
        var ours = harness.Manifest.Payloads.Select(p => ($"local/{p.Id}", p.DelayAfterMs));
        var prefix = AutoloadListBuilder.Build(ours, null);
        var count = (AutoloadListBuilder.MaxListLength - Encoding.UTF8.GetByteCount(prefix) - 1) / 2 + 1;
        var existing = new string('é', count);
        var built = AutoloadListBuilder.Build(ours, existing);

        Assert.True(built.Length <= AutoloadListBuilder.MaxListLength);
        Assert.False(AutoloadListBuilder.Fits(built));
        return existing;
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
    public async Task Unchanged_app_still_triggers_a_rescan_and_says_so()
    {
        using var harness = Ready();
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        harness.Probe.Posts.Clear();

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains("7007/api/rescan", harness.Probe.Posts);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus install" && l.Status == SetupStatus.Ok && l.Message.Contains("library rescan", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Voidshell_rescan_is_still_requested_when_the_title_is_not_confirmed_in_time()
    {
        using var harness = Ready();
        harness.VoidShell.SetUp([]);

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains("7007/api/rescan", harness.Probe.Posts);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus install" && l.Status == SetupStatus.Warn);
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
    public async Task Different_installed_app_is_kept_and_points_to_update()
    {
        using var harness = Ready();
        const string Older = """{"titleId":"PPSA99581","contentVersion":"01.008.000"}""";
        harness.Files.AddFile("/data/homebrew/PPSA99581/sce_sys/param.json", Older);
        harness.Files.AddFile("/data/homebrew/PPSA99581/models/keep.gguf", "model");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(Older, harness.Files.Text("/data/homebrew/PPSA99581/sce_sys/param.json"));
        Assert.Equal("model", harness.Files.Text("/data/homebrew/PPSA99581/models/keep.gguf"));
        Assert.Null(harness.Files.Text("/data/homebrew/PPSA99581/eboot.bin"));
        var line = Assert.Single(report.Lines, l => l.Component == "PS5LM Plus");
        Assert.Equal(SetupStatus.Warn, line.Status);
        Assert.Contains("Installed 0.8.0, pinned v0.8.3", line.Message, StringComparison.Ordinal);
        Assert.Contains("update --apps", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Newer_installed_app_is_kept_without_asking_for_an_upgrade()
    {
        using var harness = Ready();
        const string Newer = """{"titleId":"PPSA99581","contentVersion":"01.009.000"}""";
        harness.Files.AddFile("/data/homebrew/PPSA99581/sce_sys/param.json", Newer);

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(Newer, harness.Files.Text("/data/homebrew/PPSA99581/sce_sys/param.json"));
        Assert.Contains(report.Lines, l => l.Component == "PS5LM Plus" && l.Status == SetupStatus.Warn && l.Message.Contains("newer than pinned v0.8.3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Force_replaces_a_different_installed_app_and_models_survive()
    {
        using var harness = Ready();
        harness.Files.AddFile("/data/homebrew/PPSA99581/sce_sys/param.json", "PARAM-old");
        harness.Files.AddFile("/data/homebrew/PPSA99581/models/keep.gguf", "model");

        await harness.Runner(SetupHarness.Options() with { Force = true }).RunAsync(CancellationToken.None);

        Assert.Equal(SetupHarness.ParamV1, harness.Files.Text("/data/homebrew/PPSA99581/sce_sys/param.json"));
        Assert.Equal("model", harness.Files.Text("/data/homebrew/PPSA99581/models/keep.gguf"));
        Assert.Equal("ELF", harness.Files.Text("/data/homebrew/PPSA99581/eboot.bin"));
        Assert.DoesNotContain(harness.Files.Files.Keys, key => key.StartsWith("/data/ps5lm-companion", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Skip_app_still_switches_off_bundled_kstuff_and_leaves_homebrew_alone()
    {
        using var harness = Ready();

        var report = await harness.Runner(SetupHarness.Options(app: null) with { SkipApp = true }).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Contains("[kstuff]\nautoload=false\n", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.DoesNotContain("PPSA99581", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Equal(string.Empty, harness.Files.Text("/data/OnionHEN/no_kstuff"));
        Assert.Equal(OnionConfig, harness.Files.Text("/data/OnionHEN/config.ini.bak"));
        Assert.DoesNotContain(harness.Files.Files.Keys, key => key.StartsWith("/data/homebrew", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_longer_autoload_countdown_is_kept()
    {
        using var harness = Ready();
        harness.Autoload = new(false, "local/mine", 15);

        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(15, harness.Autoload.DelaySeconds);
        Assert.Contains("local/mine", harness.Autoload.List, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bundled_kstuff_is_left_alone_when_the_manifest_has_no_kstuff_lite()
    {
        using var harness = Ready();
        harness.Manifest = harness.Manifest with
        {
            Payloads = harness.Manifest.Payloads.Where(payload => payload.Id != "kstuff-lite").ToList(),
        };

        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.DoesNotContain("[kstuff]", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Null(harness.Files.Text("/data/OnionHEN/no_kstuff"));
        Assert.Contains("PPSA99581", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Contains("load:onionhen.elf", harness.Events);
    }

    [Fact]
    public async Task Missing_onionhen_config_lets_onionhen_create_it_and_then_switches_bundled_kstuff_off()
    {
        using var harness = SetupHarness.Fresh();
        harness.VoidShell.SetUp([SetupHarness.TitleId]);

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.False(harness.ConfigExistedBeforeOnionLaunch);
        Assert.DoesNotContain("load:kstuff-lite.elf", harness.Events);
        Assert.Contains("load:onionhen.elf", harness.Events);
        Assert.Contains("load:shadowmountplus.elf", harness.Events);
        Assert.Contains("[kstuff]\nautoload=false\n", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Contains("PPSA99581", harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Contains("local/kstuff-lite", harness.Autoload.List, StringComparison.Ordinal);
        var onion = harness.Events.IndexOf("load:onionhen.elf");
        var shell = harness.Events.IndexOf("load:voidshell.elf");
        Assert.True(onion >= 0 && shell > onion);
        KstuffSafety.AssertBoot(harness, report.ExitCode);
    }

    [Fact]
    public async Task Fresh_setup_without_onionhen_launches_kstuff_lite_and_does_not_wait_for_config()
    {
        using var harness = SetupHarness.Fresh();
        harness.Manifest = harness.Manifest with
        {
            Payloads = harness.Manifest.Payloads.Where(payload => payload.Id != "onionhen").ToList(),
        };
        harness.VoidShell.SetUp([SetupHarness.TitleId]);

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains("load:kstuff-lite.elf", harness.Events);
        Assert.DoesNotContain("load:onionhen.elf", harness.Events);
        Assert.Contains("load:voidshell.elf", harness.Events);
        Assert.True(harness.Events.IndexOf("load:voidshell.elf") > harness.Events.IndexOf("load:kstuff-lite.elf"));
        Assert.Contains("local/kstuff-lite", harness.Autoload.List, StringComparison.Ordinal);
        Assert.DoesNotContain("local/onionhen", harness.Autoload.List, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Lines, line => line.Message.Contains("not confirmed off", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Messages, message => message.Contains("stays on for this boot", StringComparison.Ordinal));
        KstuffSafety.AssertBoot(harness, report.ExitCode);
    }

    [Fact]
    public async Task Unready_kstuff_skips_onionhen_and_leaves_bundled_kstuff_off()
    {
        using var harness = Ready();
        harness.LoadFails.Add("kstuff-lite.elf");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(harness.Events, e => e is "load:onionhen.elf" or "load:shadowmountplus.elf" or "load:ps5-web-file-manager.elf");
        Assert.Contains("load:ftpsrv.elf", harness.Events);
        Assert.Contains("[kstuff]\nautoload=false\n", harness.Files.Text("/data/OnionHEN/config.ini"));
        var line = Assert.Single(report.Lines, l => l.Component == "Launch");
        Assert.Equal(SetupStatus.Fail, line.Status);
        Assert.Contains("Bundled kstuff stays off", line.Message, StringComparison.Ordinal);
        Assert.Contains(report.Lines, l => l.Component == "OnionHEN" && l.Status == SetupStatus.Fail && l.Message.Contains("kstuff-lite is not ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bundled_kstuff_is_not_edited_when_ftp_is_down()
    {
        using var harness = Ready();
        harness.LoadFails.Add("ftpsrv.elf");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        var line = Assert.Single(report.Lines, l => l.Component == "OnionHEN kstuff");
        Assert.Equal(SetupStatus.Warn, line.Status);
        Assert.Contains("ftpsrv is not answering", line.Message, StringComparison.Ordinal);
        Assert.Equal(OnionConfig, harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.Null(harness.Files.Text("/data/OnionHEN/no_kstuff"));
        Assert.DoesNotContain("load:onionhen.elf", harness.Events);
        var skipped = Assert.Single(report.Lines, l => l.Component == "Launch");
        Assert.Equal(SetupStatus.Fail, skipped.Status);
        Assert.Contains("not confirmed off", skipped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unresolved_kstuff_is_a_launch_failure_and_skips_onionhen()
    {
        using var harness = Ready();
        harness.UploadDropped.Add("kstuff-lite.elf");

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.DoesNotContain("load:onionhen.elf", harness.Events);
        Assert.Contains("[kstuff]\nautoload=false\n", harness.Files.Text("/data/OnionHEN/config.ini"));
        var line = Assert.Single(report.Lines, l => l.Component == "Launch");
        Assert.Equal(SetupStatus.Fail, line.Status);
        Assert.Contains("did not resolve", line.Message, StringComparison.Ordinal);
        Assert.Contains("does not list it after the upload", line.Message, StringComparison.Ordinal);
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

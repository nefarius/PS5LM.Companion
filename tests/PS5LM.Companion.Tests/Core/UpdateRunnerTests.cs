using System.Text;

using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class UpdateRunnerTests
{
    private const string IndexPath = "/data/voidshell/www/index.html";
    private const string ParamPath = "/data/homebrew/PPSA99581/sce_sys/param.json";
    private const string OldParam = "{\"titleId\":\"PPSA99581\",\"v\":0}";

    /// <summary>A console that setup has finished, with the stored payload files holding the pinned bytes.</summary>
    private static async Task<SetupHarness> PreparedAsync()
    {
        var harness = new SetupHarness();
        harness.VoidShell.SetUp([SetupHarness.TitleId]);
        harness.Files.AddFile("/data/OnionHEN/config.ini", "[app_jailbreak]\nexact_title_ids = PPSA00001\n");
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        foreach (var payload in harness.Manifest.Payloads)
        {
            var local = await harness.Artifacts.FetchAsync(payload.Url, payload.Sha256, CancellationToken.None);
            var stored = harness.Stored.Single(s => s.FileName == payload.UploadName);
            harness.Files.AddFile(stored.Path!, await File.ReadAllBytesAsync(local));
        }

        harness.Events.Clear();
        return harness;
    }

    private static UpdateRunner Runner(SetupHarness h, bool apps = false, bool check = false, string? variant = null, bool restart = false, bool force = false) => new(
        h.Manifest,
        new UpdateOptions("ps5.test", variant) { IncludeApps = apps, CheckOnly = check, Restart = restart, Force = force, WaitOverride = TimeSpan.FromMilliseconds(2) },
        new SetupServices(h.Manager, h.Artifacts, h.Files, h.Probe, h.Elfldr, h.VoidShell, new SetupHarness.FakeUi(h), TimeProvider.System));

    private static void Stale(SetupHarness h, string fileName) =>
        h.Files.AddFile(h.Stored.Single(s => s.FileName == fileName).Path!, "old build");

    [Fact]
    public async Task Current_console_changes_nothing()
    {
        using var harness = await PreparedAsync();

        var report = await Runner(harness, apps: true).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Empty(harness.Events);
        Assert.DoesNotContain(report.Lines, l => l.Status != SetupStatus.Ok && l.Component != "PS5LM");
    }

    [Fact]
    public async Task Stale_payload_that_is_not_running_is_uploaded_and_launched()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "shadowmountplus.elf");
        harness.Processes.Remove("shadowmountplus.elf");

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(["upload:shadowmountplus.elf", "load:shadowmountplus.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Ok && l.Message.StartsWith("Updated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stale_payload_that_is_running_is_uploaded_with_a_reboot_warning()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "kstuff-lite.elf");

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:kstuff-lite.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "kstuff-lite" && l.Status == SetupStatus.Warn && l.Message.Contains("cannot replace it while it runs", StringComparison.Ordinal));
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task Refused_upload_falls_back_to_replacing_the_stored_file_over_ftp()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "voidshell.elf");
        harness.UploadFails.Add("voidshell.elf");
        var path = harness.Stored.Single(s => s.FileName == "voidshell.elf").Path!;

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal("ELF:voidshell.elf", harness.Files.Text(path));
        Assert.DoesNotContain(path + ".part", harness.Files.Files.Keys);
        Assert.DoesNotContain(report.Lines, l => l.Status == SetupStatus.Fail);
    }

    private const string PidFile = "/data/voidshell/voidshell.pid";

    private static SetupHarness RunningStaleVoidShell(SetupHarness harness, string pidText = "116", int? runningPid = 116)
    {
        Stale(harness, "voidshell.elf");
        harness.Files.AddFile(PidFile, pidText);
        if (runningPid is { } pid)
        {
            // elfldr names every payload it loads "payload.elf" (seen in /processes_list on a console).
            harness.Pids[pid] = "payload.elf";
            harness.PidPayloads[pid] = "voidshell.elf";
        }

        return harness;
    }

    [Fact]
    public async Task Restart_stops_the_running_copy_by_its_pid_file_and_launches_the_new_one()
    {
        using var harness = RunningStaleVoidShell(await PreparedAsync());

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:voidshell.elf", "kill:116", "load:voidshell.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "VoidShell" && l.Status == SetupStatus.Ok && l.Message.Contains("old copy was stopped", StringComparison.Ordinal));
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task Running_payload_is_not_restarted_without_the_restart_option()
    {
        using var harness = RunningStaleVoidShell(await PreparedAsync());

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:voidshell.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "VoidShell" && l.Status == SetupStatus.Warn && l.Message.Contains("--restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_refuses_a_pid_that_is_not_running()
    {
        using var harness = RunningStaleVoidShell(await PreparedAsync(), runningPid: null);

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:voidshell.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "VoidShell" && l.Status == SetupStatus.Warn && l.Message.Contains("not running", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ftpsrv.elf")]
    [InlineData("shadowmountplus.elf")]
    [InlineData("pldmgr.elf")]
    [InlineData("elfldr.elf")]
    [InlineData("mini-syscore.elf")]
    [InlineData("SceShellUI")]
    [InlineData("/data/voidshell.elf")]
    public async Task Restart_refuses_a_pid_whose_process_name_does_not_match_the_payload(string processName)
    {
        using var harness = RunningStaleVoidShell(await PreparedAsync());
        harness.Pids[116] = processName;

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:voidshell.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "VoidShell" && l.Status == SetupStatus.Warn && l.Message.Contains("Not restarted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not a pid")]
    [InlineData("1")]
    public async Task Restart_refuses_a_missing_or_unsafe_pid(string pidText)
    {
        using var harness = RunningStaleVoidShell(await PreparedAsync(), pidText);

        await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(harness.Events, e => e.StartsWith("kill:", StringComparison.Ordinal));
    }

    private static SetupHarness RunningStaleShadowMount(SetupHarness harness, int? pid = 42)
    {
        Stale(harness, "shadowmountplus.elf");
        if (pid is int id)
        {
            harness.Pids[id] = "shadowmountplus.elf";
        }

        return harness;
    }

    [Fact]
    public async Task Restart_relaunches_a_self_replacing_payload_and_waits_for_the_new_pid()
    {
        using var harness = RunningStaleShadowMount(await PreparedAsync());
        harness.HandoffPid = 77;

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:shadowmountplus.elf", "load:shadowmountplus.elf"], harness.Events);
        Assert.Equal("shadowmountplus.elf", harness.Pids[77]);
        Assert.DoesNotContain(42, harness.Pids.Keys);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Ok && l.Message.Contains("handed over", StringComparison.Ordinal));
        Assert.Equal(0, report.ExitCode);
    }

    private static SetupHarness RunningStaleFtpsrv(SetupHarness harness)
    {
        Stale(harness, "ftpsrv.elf");
        harness.Pids[31] = "ftpsrv.elf";
        return harness;
    }

    [Fact]
    public async Task Restart_hands_ftpsrv_over_while_a_title_is_running()
    {
        using var harness = RunningStaleFtpsrv(await PreparedAsync());
        harness.VoidShell.SetUp([SetupHarness.TitleId], runningKnown: true, active: SetupHarness.TitleId);
        harness.HandoffPid = 88;

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:ftpsrv.elf", "load:ftpsrv.elf"], harness.Events);
        Assert.Equal("ftpsrv.elf", harness.Pids[88]);
        Assert.DoesNotContain(31, harness.Pids.Keys);
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Ok && l.Message.Contains("handed over", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_hands_ftpsrv_over_when_voidshell_cannot_say()
    {
        using var harness = RunningStaleFtpsrv(await PreparedAsync());
        harness.VoidShell.Snapshot = PS5LM.Companion.Core.Abstractions.AppProviderSnapshot.Unavailable(harness.VoidShell.Kind, "down");
        harness.HandoffPid = 88;

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:ftpsrv.elf", "load:ftpsrv.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Ok);
    }

    [Fact]
    public async Task Ftpsrv_that_answers_but_fails_transfers_is_replaced_once()
    {
        using var harness = await PreparedAsync();
        var path = harness.Stored.Single(s => s.FileName == "ftpsrv.elf").Path!;
        harness.Files.Unreadable.Add(path);
        harness.Pids[31] = "ftpsrv.elf";
        harness.HandoffPid = 88;

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(1, harness.Events.Count(e => e == "upload:ftpsrv.elf"));
        Assert.Equal(1, harness.Events.Count(e => e == "load:ftpsrv.elf"));
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Ok && l.Message.Contains("handed over", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ftpsrv_recovery_waits_for_the_new_server_to_listen()
    {
        using var harness = await PreparedAsync();
        var path = harness.Stored.Single(s => s.FileName == "ftpsrv.elf").Path!;
        harness.Files.Unreadable.Add(path);
        harness.Pids[31] = "ftpsrv.elf";
        harness.HandoffPid = 88;
        harness.DropPortOnLoad = true;

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Ok && l.Message.Contains("handed over", StringComparison.Ordinal));
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Fail && l.Message.Contains("not answering", StringComparison.Ordinal));
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Warn && l.Message.StartsWith("Could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ftpsrv_recovery_replaces_a_file_in_use_over_ftp_when_ftp_answers()
    {
        using var harness = await PreparedAsync();
        var path = harness.Stored.Single(s => s.FileName == "ftpsrv.elf").Path!;
        harness.Files.Unreadable.Add(path);
        harness.UploadFails.Add("ftpsrv.elf");
        harness.Pids[31] = "ftpsrv.elf";
        harness.HandoffPid = 88;

        await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Contains((path + ".part", path), harness.Files.Moves);
        Assert.Contains("load:ftpsrv.elf", harness.Events);
    }

    [Fact]
    public async Task Dead_ftpsrv_whose_file_cannot_be_replaced_reports_a_reboot()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "ftpsrv.elf");
        harness.Ports.Remove(2121);
        harness.UploadFails.Add("ftpsrv.elf");

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(harness.Files.Moves, m => m.To.EndsWith("/ftpsrv.elf", StringComparison.Ordinal));
        Assert.DoesNotContain("load:ftpsrv.elf", harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Warn && l.Message.Contains("reboot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dead_ftpsrv_is_only_reported_when_checking()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "ftpsrv.elf");
        harness.Ports.Remove(2121);

        var report = await Runner(harness, check: true).RunAsync(CancellationToken.None);

        Assert.Empty(harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "Payloads" && l.Status == SetupStatus.Fail);
    }

    [Fact]
    public async Task Stale_ftpsrv_is_not_restarted_without_the_restart_option()
    {
        using var harness = RunningStaleFtpsrv(await PreparedAsync());

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:ftpsrv.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Warn && l.Message.Contains("--restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dead_stale_ftpsrv_is_uploaded_and_launched()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "ftpsrv.elf");
        harness.Ports.Remove(2121);

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        // The fake Payload Manager does not write the uploaded bytes to the stored file, so the later comparison still sees the old build.
        Assert.Equal(["upload:ftpsrv.elf", "load:ftpsrv.elf"], harness.Events.Take(2));
        Assert.Contains(report.Lines, l => l.Component == "ftpsrv" && l.Status == SetupStatus.Ok && l.Message.Contains("Was not answering", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_warns_when_the_self_replacing_payload_keeps_the_old_pid()
    {
        using var harness = RunningStaleShadowMount(await PreparedAsync());

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:shadowmountplus.elf", "load:shadowmountplus.elf"], harness.Events);
        Assert.Contains(42, harness.Pids.Keys);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Warn && l.Message.Contains("did not hand over", StringComparison.Ordinal));
        Assert.Equal(0, report.ExitCode);
    }

    [Fact]
    public async Task Restart_does_not_relaunch_a_self_replacing_payload_while_a_title_is_running()
    {
        using var harness = RunningStaleShadowMount(await PreparedAsync());
        harness.VoidShell.SetUp([SetupHarness.TitleId], runningKnown: true, active: SetupHarness.TitleId);
        harness.HandoffPid = 77;

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:shadowmountplus.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Warn && l.Message.Contains("a title is running", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_does_not_relaunch_a_self_replacing_payload_when_voidshell_cannot_say(bool voidShellMissing)
    {
        using var harness = RunningStaleShadowMount(await PreparedAsync());
        harness.HandoffPid = 77;
        if (voidShellMissing)
        {
            harness.VoidShell.Snapshot = PS5LM.Companion.Core.Abstractions.AppProviderSnapshot.Unavailable(harness.VoidShell.Kind, "down");
        }
        else
        {
            harness.VoidShell.SetUp([SetupHarness.TitleId], runningKnown: false);
        }

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:shadowmountplus.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Warn && l.Message.Contains("not reporting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Self_replacing_payload_is_not_restarted_without_the_restart_option()
    {
        using var harness = RunningStaleShadowMount(await PreparedAsync());

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:shadowmountplus.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Warn && l.Message.Contains("--restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_does_not_launch_a_self_replacing_payload_when_its_pid_is_not_listed()
    {
        using var harness = RunningStaleShadowMount(await PreparedAsync(), pid: null);

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:shadowmountplus.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Warn && l.Message.Contains("no running process", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_does_not_launch_a_self_replacing_payload_when_process_ids_cannot_be_read()
    {
        using var harness = RunningStaleShadowMount(await PreparedAsync());
        harness.ProcessIdsUnreadable = true;

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:shadowmountplus.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Warn && l.Message.Contains("process list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_warns_when_the_self_replacing_launch_is_refused()
    {
        using var harness = RunningStaleShadowMount(await PreparedAsync());
        harness.LoadFails.Add("shadowmountplus.elf");

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:shadowmountplus.elf"], harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "ShadowMountPlus" && l.Status == SetupStatus.Warn && l.Message.Contains("Failed to launch payload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_does_not_relaunch_when_the_old_copy_still_answers()
    {
        using var harness = RunningStaleVoidShell(await PreparedAsync());
        harness.KillsAreIneffective = true;

        var report = await Runner(harness, restart: true).RunAsync(CancellationToken.None);

        Assert.DoesNotContain("load:voidshell.elf", harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "VoidShell" && l.Status == SetupStatus.Warn && l.Message.Contains("another copy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/data/pldmgr/payloads/../../homebrew/PPSA99581/eboot.bin")]
    [InlineData("/data/homebrew/PPSA99581/eboot.bin")]
    [InlineData("/etc/passwd")]
    [InlineData("/data/pldmgr/payloads//x.elf")]
    public async Task Stored_path_outside_payload_storage_is_neither_read_nor_replaced(string path)
    {
        using var harness = await PreparedAsync();
        var index = harness.Stored.FindIndex(s => s.FileName == "voidshell.elf");
        harness.Stored[index] = harness.Stored[index] with { Path = path };
        harness.Files.AddFile(path, "keep me");
        harness.UploadFails.Add("voidshell.elf");

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal("keep me", harness.Files.Text(path));
        Assert.Empty(harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "VoidShell" && l.Status == SetupStatus.Warn && l.Message.Contains("outside", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Process_based_payload_is_left_stale_when_the_process_list_cannot_be_read()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "kstuff-lite.elf");
        harness.ProcessListUnreadable = true;

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Empty(harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "kstuff-lite" && l.Status == SetupStatus.Warn && l.Message.Contains("process list", StringComparison.Ordinal));

        // Still stale, so the next run with a readable list picks it up.
        harness.ProcessListUnreadable = false;
        harness.Processes.Remove("kstuff.elf");
        await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(["upload:kstuff-lite.elf", "load:kstuff-lite.elf"], harness.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_for_another_title_is_a_failure_even_when_checking(bool check)
    {
        using var harness = await PreparedAsync();
        harness.Manifest = harness.Manifest with
        {
            Apps = harness.Manifest.Apps.Select(a => a.Id == "ps5lmplus" ? a with { TitleId = "PPSA11111" } : a).ToList(),
        };
        harness.Files.AddFile("/data/homebrew/PPSA11111/sce_sys/param.json", OldParam);

        var report = await Runner(harness, apps: true, check: check).RunAsync(CancellationToken.None);

        Assert.Contains(report.Lines, l => l.Component == "PS5LM Plus" && l.Status == SetupStatus.Fail && l.Message.Contains("PPSA99581", StringComparison.Ordinal));
        Assert.Equal(0, report.Pending);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public async Task Payload_that_is_not_stored_is_reported_not_installed()
    {
        using var harness = await PreparedAsync();
        harness.Stored.RemoveAll(s => s.FileName == "voidshell.elf");

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Empty(harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "VoidShell" && l.Status == SetupStatus.Warn && l.Message.Contains("run setup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stale_dashboard_page_is_replaced()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(IndexPath, "old page");

        await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(Encoding.UTF8.GetString(SetupHarness.FakeStore.DashboardBytes), harness.Files.Text(IndexPath));
        Assert.DoesNotContain(IndexPath + ".part", harness.Files.Files.Keys);
    }

    [Fact]
    public async Task Check_reports_updates_with_exit_code_3_and_writes_nothing()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "kstuff-lite.elf");
        harness.Files.AddFile(IndexPath, "old page");
        harness.Files.AddFile(ParamPath, OldParam);

        var report = await Runner(harness, apps: true, check: true).RunAsync(CancellationToken.None);

        Assert.Equal(3, report.ExitCode);
        Assert.Equal(3, report.Pending);
        Assert.Empty(harness.Events);
        Assert.Equal("old page", harness.Files.Text(IndexPath));
        Assert.Equal(OldParam, harness.Files.Text(ParamPath));
        Assert.Equal(["kstuff-lite", "voidshell", "ps5lmplus"], report.Updates.Select(update => update.Id).ToArray());
    }

    [Fact]
    public async Task Check_lists_a_structured_update_for_an_older_payload_manager()
    {
        using var harness = await PreparedAsync();
        harness.Version = "0.6.0";

        var report = await Runner(harness, check: true).RunAsync(CancellationToken.None);

        Assert.Equal(3, report.ExitCode);
        var update = Assert.Single(report.Updates);
        Assert.Equal("pldmgr", update.Id);
        Assert.Equal("Payload Manager", update.Name);
        Assert.Equal(harness.Manifest.PayloadManager.Version, update.Pinned);
    }

    [Fact]
    public async Task Check_does_not_flag_payloads_whose_stored_file_cannot_be_downloaded()
    {
        using var harness = await PreparedAsync();
        foreach (var stored in harness.Stored)
        {
            harness.Files.Unreadable.Add(stored.Path!);
        }

        var report = await Runner(harness, check: true).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.Pending);
        Assert.Empty(report.Updates);
        Assert.Empty(harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "kstuff-lite" && l.Status == SetupStatus.Warn && l.Message.StartsWith("Could not read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_does_not_overwrite_a_stored_file_it_could_not_read()
    {
        using var harness = await PreparedAsync();
        var path = harness.Stored.Single(s => s.FileName == "kstuff-lite.elf").Path!;
        harness.Files.Unreadable.Add(path);

        await Runner(harness).RunAsync(CancellationToken.None);

        Assert.DoesNotContain("upload:kstuff-lite.elf", harness.Events);
    }

    [Fact]
    public async Task Update_leaves_a_payload_alone_when_its_folder_cannot_be_listed()
    {
        using var harness = await PreparedAsync();
        var path = harness.Stored.Single(s => s.FileName == "kstuff-lite.elf").Path!;
        harness.Files.Unreadable.Add(path);
        harness.Files.Unlistable.Add(path[..path.LastIndexOf('/')]);

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Empty(harness.Events);
        Assert.Contains(report.Lines, l => l.Component == "kstuff-lite" && l.Status == SetupStatus.Warn);
    }

    [Fact]
    public async Task Check_flags_a_payload_whose_folder_lists_successfully_but_is_empty()
    {
        using var harness = await PreparedAsync();
        var stored = harness.Stored.Single(s => s.FileName == "kstuff-lite.elf");
        const string folder = "/data/pldmgr/payloads/local/kstuff-lite";
        harness.Stored[harness.Stored.IndexOf(stored)] = stored with { Path = folder + "/kstuff-lite.elf" };
        harness.Files.AddDirectory(folder);
        var listing = await harness.Files.TryListAsync(folder, CancellationToken.None);
        Assert.NotNull(listing);
        Assert.Empty(listing);

        var report = await Runner(harness, check: true).RunAsync(CancellationToken.None);

        Assert.Equal("kstuff-lite", Assert.Single(report.Updates).Id);
        Assert.Empty(harness.Events);
    }

    [Fact]
    public async Task Check_does_not_flag_an_unreadable_extra_file_or_param_json()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(ParamPath, OldParam);
        harness.Files.Unreadable.Add(IndexPath);
        harness.Files.Unreadable.Add(ParamPath);

        var report = await Runner(harness, apps: true, check: true).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.Pending);
        Assert.Empty(report.Updates);
        Assert.Contains(report.Lines, l => l.Component == "PS5LM Plus" && l.Status == SetupStatus.Warn);
    }

    [Fact]
    public async Task Check_still_flags_a_stored_file_that_is_readable_but_different()
    {
        using var harness = await PreparedAsync();
        Stale(harness, "kstuff-lite.elf");

        var report = await Runner(harness, check: true).RunAsync(CancellationToken.None);

        Assert.Equal("kstuff-lite", Assert.Single(report.Updates).Id);
    }

    [Fact]
    public async Task Check_lists_no_updates_when_everything_is_current()
    {
        using var harness = await PreparedAsync();

        var report = await Runner(harness, apps: true, check: true).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(0, report.Pending);
        Assert.Empty(report.Updates);
    }

    [Fact]
    public async Task App_is_left_alone_without_the_apps_option()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(ParamPath, OldParam);

        await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(OldParam, harness.Files.Text(ParamPath));
    }

    [Fact]
    public async Task Newer_installed_app_is_not_downgraded_or_flagged()
    {
        using var harness = await PreparedAsync();
        const string Newer = """{"titleId":"PPSA99581","contentVersion":"01.012.000"}""";
        harness.Files.AddFile(ParamPath, Newer);

        var check = await Runner(harness, apps: true, check: true).RunAsync(CancellationToken.None);
        var report = await Runner(harness, apps: true).RunAsync(CancellationToken.None);

        Assert.Equal(0, check.ExitCode);
        Assert.DoesNotContain(check.Updates, update => update.Id == "ps5lmplus");
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(Newer, harness.Files.Text(ParamPath));
        Assert.Contains(report.Lines, l => l.Component == "PS5LM Plus" && l.Status == SetupStatus.Warn && l.Message.Contains("newer than pinned v0.8.3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Force_replaces_a_newer_installed_app()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(ParamPath, """{"titleId":"PPSA99581","contentVersion":"01.012.000"}""");

        var report = await Runner(harness, apps: true, force: true).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(SetupHarness.ParamV1, harness.Files.Text(ParamPath));
    }

    [Fact]
    public async Task Installed_app_is_updated_in_place_and_an_absent_variant_is_not_installed()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(ParamPath, OldParam);

        var report = await Runner(harness, apps: true).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Equal(SetupHarness.ParamV1, harness.Files.Text(ParamPath));
        Assert.DoesNotContain(harness.Files.Files.Keys, k => k.Contains("PPSA99575", StringComparison.Ordinal));
        Assert.Contains(report.Lines, l => l.Component == "PS5LM" && l.Message.Contains("Not installed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Running_app_is_not_overwritten()
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(ParamPath, OldParam);
        harness.VoidShell.SetUp([SetupHarness.TitleId], runningKnown: true, active: SetupHarness.TitleId);

        var report = await Runner(harness, apps: true).RunAsync(CancellationToken.None);

        Assert.Equal(OldParam, harness.Files.Text(ParamPath));
        Assert.Contains(report.Lines, l => l.Component == "PS5LM Plus" && l.Status == SetupStatus.Warn && l.Message.Contains("running", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task App_is_not_overwritten_when_the_running_state_is_unknown(bool voidShellAnswers)
    {
        using var harness = await PreparedAsync();
        harness.Files.AddFile(ParamPath, OldParam);
        if (voidShellAnswers)
        {
            harness.VoidShell.SetUp([SetupHarness.TitleId], runningKnown: false);
        }
        else
        {
            harness.VoidShell.Snapshot = PS5LM.Companion.Core.Abstractions.AppProviderSnapshot.Unavailable(harness.VoidShell.Kind, "down");
        }

        var report = await Runner(harness, apps: true).RunAsync(CancellationToken.None);

        Assert.Equal(OldParam, harness.Files.Text(ParamPath));
        Assert.Contains(report.Lines, l => l.Component == "PS5LM Plus" && l.Status == SetupStatus.Warn && l.Message.Contains("Cannot tell", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unprepared_console_fails_and_points_to_setup()
    {
        using var harness = new SetupHarness();
        harness.Ports.Remove(8084);

        var report = await Runner(harness).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Lines, l => l.Message.Contains("run setup", StringComparison.OrdinalIgnoreCase));
    }
}

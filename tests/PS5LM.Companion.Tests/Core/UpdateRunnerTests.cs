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

    private static UpdateRunner Runner(SetupHarness h, bool apps = false, bool check = false, string? variant = null, bool restart = false) => new(
        h.Manifest,
        new UpdateOptions("ps5.test", variant) { IncludeApps = apps, CheckOnly = check, Restart = restart, WaitOverride = TimeSpan.FromMilliseconds(2) },
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
        Assert.Contains(report.Lines, l => l.Component == "kstuff-lite" && l.Status == SetupStatus.Warn);
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

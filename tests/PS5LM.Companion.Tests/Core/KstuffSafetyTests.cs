using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

/// <summary>
/// A fresh console and the commands that touch boot settings must never leave two kstuffs loaded or armed for the next boot.
/// </summary>
public sealed class KstuffSafetyTests
{
    public enum ConsoleState
    {
        Fresh,
        Prepared,
        KstuffWithoutConfig,
        ConfigAppearsLate,
        ConfigNever,
        FtpDown,
        OldOnion,
        BootAutoloadRunning,
    }

    public enum SafetyCommand
    {
        Setup,
        Update,
        UpdateSettings,
        UpdateApps,
        UpdateRestart,
    }

    public static IEnumerable<object[]> Cases() =>
        from state in Enum.GetValues<ConsoleState>()
        from command in Enum.GetValues<SafetyCommand>()
        select new object[] { state, command };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Commands_do_not_arm_two_kstuffs(ConsoleState state, SafetyCommand command)
    {
        using var harness = await ConsoleAsync(state, command != SafetyCommand.Setup);
        var exit = command == SafetyCommand.Setup
            ? (await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None)).ExitCode
            : (await Update(harness, command).RunAsync(CancellationToken.None)).ExitCode;

        if (command is SafetyCommand.Setup or SafetyCommand.UpdateSettings)
        {
            KstuffSafety.AssertBoot(harness, exit);
        }
    }

    [Fact]
    public async Task A_config_that_never_appears_keeps_kstuff_lite_out_of_autoload()
    {
        using var harness = SetupHarness.Fresh();
        harness.ConfigCreation = OnionConfigCreation.Never;
        harness.VoidShell.SetUp([SetupHarness.TitleId]);

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Null(harness.Files.Text("/data/OnionHEN/config.ini"));
        Assert.DoesNotContain("kstuff-lite", harness.Autoload.List, StringComparison.Ordinal);
        Assert.Contains("local/onionhen", harness.Autoload.List, StringComparison.Ordinal);
        Assert.Contains(report.Lines, line => line.Status == SetupStatus.Warn && (line.Message.Contains("stays on for this boot", StringComparison.Ordinal) || line.Message.Contains("not confirmed off", StringComparison.Ordinal)));
        KstuffSafety.AssertBoot(harness, report.ExitCode);
    }

    [Fact]
    public async Task Kstuff_already_running_without_a_config_asks_for_a_reboot_and_does_not_wait()
    {
        using var harness = SetupHarness.Fresh();
        harness.Processes.Add("kstuff.elf");
        harness.KstuffCount = 1;
        harness.VoidShell.SetUp([SetupHarness.TitleId]);

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.DoesNotContain("load:onionhen.elf", harness.Events);
        Assert.DoesNotContain("load:shadowmountplus.elf", harness.Events);
        Assert.Contains("load:voidshell.elf", harness.Events);
        Assert.DoesNotContain(harness.Messages, message => message.StartsWith("Waiting for ShadowMountPlus", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Probe.Posts, post => post.Contains("rescan", StringComparison.Ordinal));
        Assert.Equal(1, harness.Files.ReadCounts.GetValueOrDefault("/data/shadowmount/debug.log"));
        Assert.Contains(report.Lines, line => line.Component == "Launch" && line.Status == SetupStatus.Fail && line.Message.Contains("Reboot the console", StringComparison.Ordinal));
        Assert.Contains(report.Lines, line => line.Component == "ShadowMountPlus install" && line.Message.Contains("is not running", StringComparison.Ordinal));
        Assert.DoesNotContain("kstuff-lite", harness.Autoload.List, StringComparison.Ordinal);
        KstuffSafety.AssertBoot(harness, report.ExitCode);
    }

    [Fact]
    public async Task Update_refuses_kstuff_lite_while_onionhen_may_still_load_its_copy()
    {
        using var harness = await PreparedUpdateConsoleAsync();
        harness.Files.AddFile("/data/OnionHEN/config.ini", "[app_jailbreak]\nexact_title_ids = PPSA00001\n[kstuff]\nautoload=true\n");
        harness.Processes.Remove("kstuff.elf");
        Stale(harness, "kstuff-lite.elf");

        var report = await Update(harness, SafetyCommand.Update).RunAsync(CancellationToken.None);

        Assert.DoesNotContain("load:kstuff-lite.elf", harness.Events);
        Assert.Contains(report.Lines, line => line.Component == "kstuff-lite" && line.Message.Contains("bundled kstuff is not confirmed off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_refuses_onionhen_while_kstuff_is_already_running()
    {
        using var harness = await PreparedUpdateConsoleAsync();
        await harness.Files.DeleteAsync("/data/OnionHEN/config.ini", CancellationToken.None);
        harness.Processes.Remove("onion_daemon.elf");
        Stale(harness, "onionhen.elf");

        var report = await Update(harness, SafetyCommand.Update).RunAsync(CancellationToken.None);

        Assert.DoesNotContain("load:onionhen.elf", harness.Events);
        Assert.Contains(report.Lines, line => line.Component == "OnionHEN" && line.Message.Contains("Reboot the console", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_are_applied_before_an_ftpsrv_handoff()
    {
        using var harness = await PreparedUpdateConsoleAsync();
        harness.Autoload = harness.Autoload with { List = harness.Autoload.List.Replace("!8000", "!1000", StringComparison.Ordinal) };
        Stale(harness, "ftpsrv.elf");
        harness.Pids[31] = "ftpsrv.elf";
        harness.HandoffPid = 88;

        await Update(harness, SafetyCommand.UpdateRestart, settings: true).RunAsync(CancellationToken.None);

        var switched = harness.Messages.FindIndex(message => message.Contains("bundled kstuff", StringComparison.Ordinal));
        var autoload = harness.Messages.FindIndex(message => message.StartsWith("Enabling autoload", StringComparison.Ordinal));
        var handoff = harness.Messages.FindIndex(message => message.Contains("replace the running copy", StringComparison.Ordinal));
        Assert.True(switched >= 0 && autoload > switched && handoff > autoload);
        Assert.Contains("!8000", harness.Autoload.List, StringComparison.Ordinal);
    }

    private static async Task<SetupHarness> ConsoleAsync(ConsoleState state, bool update)
    {
        if (!update)
        {
            return New(state);
        }

        var harness = New(ConsoleState.Prepared);
        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);
        await SeedPayloadBytesAsync(harness);
        harness.Events.Clear();
        Apply(harness, state);
        return harness;
    }

    private static SetupHarness New(ConsoleState state)
    {
        var harness = state is ConsoleState.Prepared or ConsoleState.OldOnion
            ? SetupHarness.Prepared("[app_jailbreak]\nexact_title_ids = PPSA00001\n")
            : SetupHarness.Fresh();
        harness.VoidShell.SetUp([SetupHarness.TitleId]);
        Apply(harness, state);
        return harness;
    }

    private static void Apply(SetupHarness harness, ConsoleState state)
    {
        switch (state)
        {
            case ConsoleState.KstuffWithoutConfig:
                harness.Files.DeleteAsync("/data/OnionHEN/config.ini", CancellationToken.None).GetAwaiter().GetResult();
                harness.Processes.Add("kstuff.elf");
                harness.KstuffCount = Math.Max(harness.KstuffCount, 1);
                break;
            case ConsoleState.ConfigAppearsLate:
                harness.Files.DeleteAsync("/data/OnionHEN/config.ini", CancellationToken.None).GetAwaiter().GetResult();
                harness.ConfigCreation = OnionConfigCreation.OnSecondRead;
                break;
            case ConsoleState.ConfigNever:
                harness.Files.DeleteAsync("/data/OnionHEN/config.ini", CancellationToken.None).GetAwaiter().GetResult();
                harness.ConfigCreation = OnionConfigCreation.Never;
                break;
            case ConsoleState.FtpDown:
                harness.Ports.Remove(2121);
                harness.LoadFails.Add("ftpsrv.elf");
                break;
            case ConsoleState.OldOnion:
                var onion = harness.Stored.FirstOrDefault(item => item.FileName == "onionhen.elf");
                if (onion is not null)
                {
                    harness.Stored.Remove(onion);
                    harness.Stored.Add(onion with { Slot = "repo/onionhen@v0.0.13" });
                }
                else
                {
                    harness.Stored.Add(new ManagedPayload("repo/onionhen@v0.0.13", "onionhen", "onionhen.elf", "/data/pldmgr/payloads/onionhen.elf"));
                }

                break;
            case ConsoleState.BootAutoloadRunning:
                harness.AutoloadRuns.Enqueue(new AutoloadRun("countdown", 7, "", 0, 7));
                harness.AutoloadRuns.Enqueue(new AutoloadRun("executing", 0, "local/kstuff-lite", 3, 7));
                harness.AutoloadRuns.Enqueue(new AutoloadRun("done", 0, "", 7, 7));
                harness.OnAutoloadFinished = harness.FinishBootAutoload;
                break;
        }
    }

    private static async Task<SetupHarness> PreparedUpdateConsoleAsync()
    {
        var harness = await ConsoleAsync(ConsoleState.Prepared, update: true);
        harness.Events.Clear();
        return harness;
    }

    private static async Task SeedPayloadBytesAsync(SetupHarness harness)
    {
        foreach (var payload in harness.Manifest.Payloads)
        {
            var local = await harness.Artifacts.FetchAsync(payload.Url, payload.Sha256, CancellationToken.None);
            var stored = harness.Stored.Single(item => item.FileName == payload.UploadName);
            harness.Files.AddFile(stored.Path!, await File.ReadAllBytesAsync(local));
        }
    }

    private static void Stale(SetupHarness harness, string fileName) =>
        harness.Files.AddFile(harness.Stored.Single(item => item.FileName == fileName).Path!, "old build"u8.ToArray());

    private static UpdateRunner Update(SetupHarness harness, SafetyCommand command, bool settings = false) => new(
        harness.Manifest,
        new UpdateOptions("ps5.test", "ps5lmplus")
        {
            IncludeApps = command == SafetyCommand.UpdateApps,
            IncludeSettings = settings || command == SafetyCommand.UpdateSettings,
            Restart = command == SafetyCommand.UpdateRestart,
            WaitOverride = TimeSpan.FromMilliseconds(2),
        },
        new SetupServices(harness.Manager, harness.Artifacts, harness.Files, harness.Probe, harness.Elfldr, harness.VoidShell, new SetupHarness.FakeUi(harness), TimeProvider.System));
}

using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class BootAutoloadTests
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
    public async Task Setup_waits_for_a_boot_autoload_and_does_not_send_a_second_kstuff()
    {
        using var harness = Ready();
        harness.AutoloadRuns.Enqueue(new AutoloadRun("countdown", 7, "", 0, 7));
        harness.AutoloadRuns.Enqueue(new AutoloadRun("executing", 0, "local/kstuff-lite", 3, 7));
        harness.AutoloadRuns.Enqueue(new AutoloadRun("done", 0, "", 7, 7));
        harness.OnAutoloadFinished = harness.FinishBootAutoload;

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.DoesNotContain("load:kstuff-lite.elf", harness.Events);
        Assert.DoesNotContain("load:onionhen.elf", harness.Events);
        Assert.Contains(report.Lines, line => line.Component == "kstuff-lite" && line.Status == SetupStatus.Ok && line.Message.Contains("Running", StringComparison.Ordinal));
        Assert.Contains(report.Lines, line => line.Component == "OnionHEN" && line.Status == SetupStatus.Ok && line.Message.Contains("Running", StringComparison.Ordinal));
        var waits = harness.Messages.Where(message => message.Contains("boot autoload is running", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, waits.Count);
        Assert.Contains("countdown 7 s", waits[0], StringComparison.Ordinal);
        Assert.Contains("3 of 7, local/kstuff-lite", waits[1], StringComparison.Ordinal);
        KstuffSafety.AssertBoot(harness, report.ExitCode);
    }

    [Fact]
    public async Task A_boot_autoload_that_does_not_finish_stops_setup_before_any_launch()
    {
        using var harness = Ready();
        harness.Version = "0.5.2";
        harness.CanPrompt = true;
        harness.Consent = true;
        harness.AutoloadRuns.Enqueue(new AutoloadRun("executing", 0, "local/kstuff-lite", 1, 7));

        var report = await harness.Runner(SetupHarness.Options() with { UpgradePayloadManager = true }).RunAsync(CancellationToken.None);

        Assert.Equal(2, report.ExitCode);
        Assert.Contains(report.Lines, line => line.Component == "Autoload" && line.Status == SetupStatus.Fail && line.Message.Contains("still running", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Events, e => e.StartsWith("load:", StringComparison.Ordinal) || e.StartsWith("upload:", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.Messages, message => message.Contains("Upgraded", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("aborted")]
    [InlineData("failed")]
    [InlineData("disabled")]
    public async Task A_finished_boot_autoload_does_not_delay_setup(string state)
    {
        using var harness = Ready();
        harness.AutoloadRuns.Enqueue(new AutoloadRun(state, 0, "", 7, 7));

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Contains("load:kstuff-lite.elf", harness.Events);
        Assert.DoesNotContain(harness.Messages, message => message.Contains("boot autoload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_legacy_countdown_is_waited_out_and_a_zero_countdown_is_not()
    {
        using var waiting = Ready();
        waiting.AutoloadRuns.Enqueue(new AutoloadRun(null, 5, null, 0, 0));
        waiting.AutoloadRuns.Enqueue(new AutoloadRun(null, 0, null, 0, 0));

        await waiting.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains(waiting.Messages, message => message.Contains("countdown 5 s", StringComparison.Ordinal));
        Assert.Contains("load:kstuff-lite.elf", waiting.Events);

        using var idle = Ready();
        idle.AutoloadRuns.Enqueue(new AutoloadRun(null, 0, null, 0, 0));

        await idle.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.DoesNotContain(idle.Messages, message => message.Contains("boot autoload", StringComparison.Ordinal));
        Assert.Contains("load:kstuff-lite.elf", idle.Events);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(3600, 3600)]
    [InlineData(99999, 3600)]
    public void A_reported_countdown_outside_an_hour_does_not_extend_the_wait(int reported, int allowance)
    {
        Assert.Equal(allowance, SetupRunner.CountdownAllowance(reported));
    }

    [Fact]
    public async Task An_unrecognized_autoload_state_is_reported_and_does_not_block_setup()
    {
        using var harness = Ready();
        harness.AutoloadRuns.Enqueue(new AutoloadRun("paused", 4, null, 0, 0) { Understood = false });

        var report = await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Equal(0, report.ExitCode);
        Assert.Contains("load:kstuff-lite.elf", harness.Events);
        Assert.Contains(harness.Messages, message => message.Contains("state 'paused'", StringComparison.Ordinal) && message.Contains("will not wait", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_countdown_without_a_state_is_waited_out_and_its_kstuff_is_not_sent_again()
    {
        using var harness = Ready();
        harness.AutoloadRuns.Enqueue(new AutoloadRun(null, 5, null, 0, 0));
        harness.AutoloadRuns.Enqueue(new AutoloadRun("done", 0, "", 1, 1));
        harness.OnAutoloadFinished = harness.FinishBootAutoload;

        await harness.Runner(SetupHarness.Options()).RunAsync(CancellationToken.None);

        Assert.Contains(harness.Messages, message => message.Contains("countdown 5 s", StringComparison.Ordinal));
        Assert.DoesNotContain("load:kstuff-lite.elf", harness.Events);
    }
}

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Infrastructure;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class ConsoleConnectionProbeTests
{
    [Fact]
    public async Task A_reachable_console_passes_except_the_closed_llama_port()
    {
        var connectors = new FakeConnectorFactory();
        var fake = connectors[ConsoleConnectionProbe.ProbeId];
        fake.Payloads.Snapshot = new PayloadCatalogSnapshot(true, true, null, [], new HashSet<string>());
        fake.Elfldr.Listening = true;
        fake.VoidShell.SetUp([]);
        fake.Websrv.SetUp([]);
        fake.Sensors.Sensors = new ConsoleSensors(40, null, null, null, []);
        var probe = new ConsoleConnectionProbe(connectors);

        var report = await probe.TestAsync(new PS5Options { Host = "10.0.0.8" }, CancellationToken.None);

        Assert.Equal(
            [ProbeOutcome.Pass, ProbeOutcome.Pass, ProbeOutcome.Pass, ProbeOutcome.Pass, ProbeOutcome.Pass, ProbeOutcome.Pass, ProbeOutcome.Skipped],
            report.Checks.Select(check => check.Outcome));
        Assert.Contains("PS5LM", report.Checks[0].Detail, StringComparison.Ordinal);
        Assert.Contains("Closed until a model is loaded", report.Checks[^1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_console_fails_the_checks_that_need_it()
    {
        var connectors = new FakeConnectorFactory();
        connectors[ConsoleConnectionProbe.ProbeId].FileSystem.Unreachable = true;
        var probe = new ConsoleConnectionProbe(connectors);

        var report = await probe.TestAsync(new PS5Options { Host = "10.0.0.9", VoidShellPort = 0, WebsrvPort = 0 }, CancellationToken.None);

        Assert.Equal(ProbeOutcome.Fail, report.Checks.Single(check => check.Name == "FTP").Outcome);
        Assert.Equal(ProbeOutcome.Skipped, report.Checks.Single(check => check.Name == "VoidShell").Outcome);
        Assert.Equal(ProbeOutcome.Skipped, report.Checks.Single(check => check.Name == "websrv").Outcome);
        Assert.Equal(ProbeOutcome.Skipped, report.Checks.Single(check => check.Name == "llama-server").Outcome);
    }

    [Fact]
    public async Task A_websrv_that_does_not_answer_is_skipped_while_VoidShell_works_and_fails_without_it()
    {
        var withVoidShell = new FakeConnectorFactory();
        withVoidShell[ConsoleConnectionProbe.ProbeId].VoidShell.SetUp([]);
        var without = new FakeConnectorFactory();

        var covered = await new ConsoleConnectionProbe(withVoidShell).TestAsync(new PS5Options { Host = "10.0.0.8" }, CancellationToken.None);
        var uncovered = await new ConsoleConnectionProbe(without).TestAsync(new PS5Options { Host = "10.0.0.8" }, CancellationToken.None);

        var skipped = covered.Checks.Single(check => check.Name == "websrv");
        Assert.Equal(ProbeOutcome.Skipped, skipped.Outcome);
        Assert.Contains("VoidShell", skipped.Detail, StringComparison.Ordinal);
        Assert.Equal(ProbeOutcome.Fail, uncovered.Checks.Single(check => check.Name == "websrv").Outcome);
    }

    [Fact]
    public async Task A_host_that_cannot_be_addressed_fails_before_any_connection()
    {
        var probe = new ConsoleConnectionProbe(new ThrowingConnectorFactory());

        var report = await probe.TestAsync(new PS5Options { Host = "bad host" }, CancellationToken.None);

        var check = Assert.Single(report.Checks);
        Assert.Equal("Settings", check.Name);
        Assert.Equal(ProbeOutcome.Fail, check.Outcome);
    }

    private sealed class ThrowingConnectorFactory : IConsoleConnectorFactory
    {
        public ConsoleConnectors Create(string consoleId, PS5Options options) =>
            throw new UriFormatException("Invalid host.");
    }
}

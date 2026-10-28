using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class PayloadManagerRestartTests
{
    private static readonly ManifestPayloadManager Pinned = new("0.6.3", "0.5.0", "pldmgr_v0.6.3.elf", "https://example.test/pldmgr_v0.6.3.elf", "abc");

    private sealed class Console : IPayloadManagerAdmin, IConsoleProbe, IProcessControl, IElfldrSender, IArtifactStore
    {
        public bool PortOpen { get; set; } = true;

        public bool ShutdownWorks { get; set; }

        public bool ProcessApi { get; set; } = true;

        public bool KillWorks { get; set; } = true;

        public bool ElfldrWorks { get; set; } = true;

        public bool NewManagerAnswers { get; set; } = true;

        public bool ElfldrAccepts { get; set; } = true;

        public Task<bool> AcceptsAsync(CancellationToken cancellationToken) => Task.FromResult(ElfldrAccepts);

        public bool Started { get; private set; }

        public List<int> Killed { get; } = [];

        public List<string> Sent { get; } = [];

        public Task<string?> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(Started && NewManagerAnswers ? "0.6.3" : null);

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            if (!ShutdownWorks)
            {
                throw new IOException("stuck");
            }

            PortOpen = false;
            return Task.CompletedTask;
        }

        public Task<bool> PortOpenAsync(int port, CancellationToken cancellationToken) => Task.FromResult(PortOpen);

        public Task<IReadOnlyList<RemoteProcess>?> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteProcess>?>(ProcessApi ? [new(1, "init"), new(77, "pldmgr_v0.6.3.el"), new(90, "ftpsrv.elf")] : null);

        public Task<bool> KillAsync(int pid, CancellationToken cancellationToken)
        {
            Killed.Add(pid);
            if (KillWorks)
            {
                PortOpen = false;
            }

            return Task.FromResult(KillWorks);
        }

        public Task SendAsync(string filePath, CancellationToken cancellationToken)
        {
            if (!ElfldrWorks)
            {
                throw new IOException("refused");
            }

            Sent.Add(filePath);
            Started = true;
            PortOpen = true;
            return Task.CompletedTask;
        }

        public Task<string> FetchAsync(string url, string sha256, CancellationToken cancellationToken) => Task.FromResult("cache/" + sha256);

        public Task<IReadOnlyList<ManagedPayload>> ListPayloadsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task UploadAsync(string fileName, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PayloadLaunchResult> LoadAsync(string target, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlySet<string>?> GetProcessesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, string>?> GetProcessIdsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> KillProcessAsync(int pid, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AutoloadConfig?> GetAutoloadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetAutoloadAsync(bool enabled, string list, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HtmlAsync(int port, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> PostAsync(int port, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static PayloadManagerRestartService Service(Console console, bool elfldrListening = true, bool withProcesses = true)
    {
        var options = new PS5Options { Host = "ps5" };
        var time = TimeProvider.System;
        var status = new CompanionStatusService(
            options,
            ConsoleFixtures.PS5LMInstalled(),
            new FakeLlamaProbe(),
            new FakeSensorProbe(),
            new FakePayloadCatalog(),
            new FakeElfldrProbe { Listening = elfldrListening },
            new LoadTracker(time),
            time);
        var recovery = new PayloadManagerRecovery(console, console, withProcesses ? console : null, console, console, Pinned);
        return new PayloadManagerRestartService(options, recovery, new FakeElfldrProbe { Listening = elfldrListening }, status, new CompanionStatusStore(), time)
        {
            WaitOverride = TimeSpan.FromMilliseconds(5),
        };
    }

    [Fact]
    public async Task A_manager_that_exits_on_request_is_replaced_without_killing_anything()
    {
        var console = new Console { ShutdownWorks = true };

        var result = await Service(console).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.Empty(console.Killed);
        Assert.Equal(["cache/abc"], console.Sent);
    }

    [Fact]
    public async Task A_stuck_manager_is_killed_through_voidshell_by_its_process_name()
    {
        var console = new Console();

        var result = await Service(console).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.Equal([77], console.Killed);
        Assert.True(console.Started);
    }

    [Fact]
    public async Task A_closed_port_skips_the_stop_and_just_starts_the_manager()
    {
        var console = new Console { PortOpen = false };

        var result = await Service(console).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.Empty(console.Killed);
    }

    [Fact]
    public async Task Without_the_process_api_a_stuck_manager_points_at_update()
    {
        var console = new Console { ProcessApi = false };

        var result = await Service(console).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("update", result.Message, StringComparison.Ordinal);
        Assert.Empty(console.Sent);
    }

    [Fact]
    public async Task Without_voidshell_a_stuck_manager_is_not_replaced()
    {
        var console = new Console();

        var result = await Service(console, withProcesses: false).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Empty(console.Sent);
    }

    [Fact]
    public async Task A_port_that_stays_open_after_the_kill_is_reported()
    {
        var console = new Console { KillWorks = false };

        var result = await Service(console).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("Reboot", result.Message, StringComparison.Ordinal);
        Assert.Empty(console.Sent);
    }

    [Fact]
    public async Task Elfldr_down_is_refused_before_anything_is_stopped()
    {
        var console = new Console();

        var result = await Service(console, elfldrListening: false).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("elfldr", result.Message, StringComparison.Ordinal);
        Assert.Empty(console.Killed);
    }

    [Fact]
    public async Task An_elfldr_that_closes_connections_at_once_is_refused_before_anything_is_stopped()
    {
        var console = new Console { ElfldrAccepts = false, ShutdownWorks = true };

        var result = await Service(console).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("Nothing was stopped", result.Message, StringComparison.Ordinal);
        Assert.True(console.PortOpen);
        Assert.Empty(console.Killed);
        Assert.Empty(console.Sent);
    }

    [Fact]
    public async Task A_new_manager_that_never_answers_is_a_failure()
    {
        var console = new Console { NewManagerAnswers = false };

        var result = await Service(console).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("did not answer", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_elfldr_send_failure_is_reported_after_the_stop()
    {
        var console = new Console { ElfldrWorks = false };

        var result = await Service(console).RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("elfldr failed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_console_without_recovery_support_says_so()
    {
        var options = new PS5Options { Host = "ps5" };
        var status = new CompanionStatusService(
            options, ConsoleFixtures.PS5LMInstalled(), new FakeLlamaProbe(), new FakeSensorProbe(), new FakePayloadCatalog(),
            new FakeElfldrProbe(), new LoadTracker(TimeProvider.System), TimeProvider.System);

        var result = await new PayloadManagerRestartService(options, null, new FakeElfldrProbe(), status, new CompanionStatusStore(), TimeProvider.System)
            .RestartAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
    }
}

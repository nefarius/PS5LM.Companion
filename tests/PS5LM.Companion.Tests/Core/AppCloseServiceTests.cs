using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class AppCloseServiceTests
{
    private const string QuitFile = "/data/PS5LM/quit";

    private static (AppCloseService Service, FakeFileSystem Fs, CompanionStatusStore Store) Harness()
    {
        var fs = ConsoleFixtures.PS5LMInstalled();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        var tracker = new LoadTracker(time);
        var status = new CompanionStatusService(
            new PS5Options { Host = "ps5" }, fs, new FakeLlamaProbe(), new FakeSensorProbe(), new FakePayloadCatalog(), new FakeElfldrProbe(), tracker, time);
        var store = new CompanionStatusStore();
        return (new AppCloseService(fs, status, store, tracker, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(10)), fs, store);
    }

    private static Task AppPicksUpQuit(FakeFileSystem fs) => Task.Run(async () =>
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!fs.Files.ContainsKey(QuitFile))
        {
            await Task.Delay(5, timeout.Token);
        }

        await fs.DeleteAsync(QuitFile, CancellationToken.None);
    });

    [Fact]
    public async Task A_quit_request_the_app_takes_is_accepted()
    {
        var (service, fs, _) = Harness();
        var app = AppPicksUpQuit(fs);

        var result = await service.CloseAsync(PS5Variant.PS5LM, CancellationToken.None);
        await app;

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.DoesNotContain(QuitFile, fs.Files.Keys);
    }

    [Fact]
    public async Task A_request_nobody_takes_is_withdrawn()
    {
        var (service, fs, _) = Harness();

        var result = await service.CloseAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.DoesNotContain(QuitFile, fs.Files.Keys);
    }

    [Fact]
    public async Task A_pending_command_blocks_the_request()
    {
        var (service, fs, _) = Harness();
        fs.AddFile("/data/PS5LM/load", "/data/PS5LM/models/a.gguf\n");

        var result = await service.CloseAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, result.Outcome);
        Assert.DoesNotContain(QuitFile, fs.Files.Keys);
    }

    [Fact]
    public async Task A_variant_known_not_to_run_writes_nothing()
    {
        var (service, fs, store) = Harness();
        store.Publish(CompanionStatus.Initial with
        {
            AppControl = AppControlEvaluator.Evaluate([new(PS5LM.Companion.Core.Abstractions.AppProviderKind.VoidShell, true, "v", ["PPSA99575"], true, null, null)]),
        });

        var result = await service.CloseAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.Equal(OperationOutcome.NoChange, result.Outcome);
        Assert.DoesNotContain(QuitFile, fs.Files.Keys);
    }

    [Fact]
    public async Task A_running_app_of_unknown_variant_is_not_closed()
    {
        var (service, fs, store) = Harness();
        store.Publish(CompanionStatus.Initial with
        {
            AppControl = AppControlEvaluator.Evaluate([new(PS5LM.Companion.Core.Abstractions.AppProviderKind.VoidShell, true, "v", ["PPSA99575", "PPSA99581"], true, "PPSA99", null)]),
        });

        var result = await service.CloseAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, result.Outcome);
        Assert.DoesNotContain(QuitFile, fs.Files.Keys);
    }

    [Fact]
    public async Task Cancellation_while_waiting_withdraws_the_request()
    {
        var (service, fs, _) = Harness();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CloseAsync(PS5Variant.PS5LM, cts.Token));

        Assert.DoesNotContain(QuitFile, fs.Files.Keys);
    }

    [Fact]
    public async Task An_unknown_variant_is_invalid()
    {
        var (service, _, _) = Harness();

        Assert.Equal(OperationOutcome.Invalid, (await service.CloseAsync(PS5Variant.Unknown, CancellationToken.None)).Outcome);
    }
}

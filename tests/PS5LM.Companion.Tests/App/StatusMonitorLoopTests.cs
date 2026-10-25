using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.App.Monitoring;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.App;

public sealed class StatusMonitorLoopTests
{
    [Fact]
    public async Task Adding_a_console_starts_a_refresh_loop_and_removing_it_stops_the_loop()
    {
        var store = new MemoryConsoleStore();
        using var registry = new ConsoleRegistry(new ConsolesOptions(), new FakeConnectorFactory(), TimeProvider.System, store);
        var monitor = new StatusMonitor(registry, NullLogger<StatusMonitor>.Instance);
        await monitor.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(registry.Add("den", new PS5Options { Host = "10.0.0.8", PollIntervalSeconds = 1 }).Succeeded);
            var console = registry.Find("den")!;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (console.Store.Current.UpdatedAt == DateTimeOffset.MinValue)
                {
                    await Task.Delay(20, timeout.Token);
                }
            }

            await Task.Delay(300);
            var stamp = console.Store.Current.UpdatedAt;
            Assert.True(registry.Remove("den").Succeeded);
            await Task.Delay(1500);

            Assert.Equal(stamp, console.Store.Current.UpdatedAt);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    private sealed class MemoryConsoleStore : IConsoleStore
    {
        public IReadOnlyList<StoredConsole> Load() => [];

        public void Save(IReadOnlyList<StoredConsole> consoles)
        {
        }
    }
}

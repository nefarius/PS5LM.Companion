using PS5LM.Companion.App.Monitoring;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Tests.App;

public sealed class ComponentUpdateMonitorTests
{
    [Theory]
    [InlineData(CompanionLifecycle.AppAvailableNoModel, false, true)]
    [InlineData(CompanionLifecycle.Ready, false, true)]
    [InlineData(CompanionLifecycle.Generating, false, true)]
    [InlineData(CompanionLifecycle.Disconnected, false, false)]
    [InlineData(CompanionLifecycle.Loading, false, false)]
    [InlineData(CompanionLifecycle.Ready, true, false)]
    public void Hash_checks_wait_while_the_console_is_down_busy_or_receiving_a_transfer(
        CompanionLifecycle lifecycle,
        bool transferActive,
        bool expected)
    {
        var status = CompanionStatus.Initial with { Lifecycle = lifecycle, UpdatedAt = DateTimeOffset.UtcNow };

        Assert.Equal(expected, ComponentUpdateMonitor.CanCheck(status, transferActive));
    }

    [Fact]
    public void The_first_status_is_not_checked_until_a_refresh_has_run()
    {
        Assert.False(ComponentUpdateMonitor.CanCheck(CompanionStatus.Initial, transferActive: false));
    }
}
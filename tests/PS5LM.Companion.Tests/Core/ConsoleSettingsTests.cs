using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Tests.Core;

public sealed class ConsoleSettingsTests
{
    [Fact]
    public void The_VoidShell_port_is_required_and_websrv_can_be_disabled()
    {
        var missing = ConsoleSettings.Errors("ps5", new PS5Options { Host = "10.0.0.8", VoidShellPort = 0 });
        var fallbackOff = ConsoleSettings.Errors("ps5", new PS5Options { Host = "10.0.0.8", WebsrvPort = 0 });

        Assert.Contains(missing, error => error.Contains("VoidShell port is required", StringComparison.Ordinal));
        Assert.Empty(fallbackOff);
    }
}

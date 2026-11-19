using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Tests.Core;

public sealed class ConsoleSettingsTests
{
    [Fact]
    public void The_VoidShell_port_is_required()
    {
        var missing = ConsoleSettings.Errors("ps5", new PS5Options { Host = "10.0.0.8", VoidShellPort = 0 });
        var present = ConsoleSettings.Errors("ps5", new PS5Options { Host = "10.0.0.8" });

        Assert.Contains(missing, error => error.Contains("VoidShell port is required", StringComparison.Ordinal));
        Assert.Empty(present);
    }

    [Fact]
    public void Description_and_location_are_optional_and_length_limited()
    {
        var blank = ConsoleSettings.Errors("ps5", new PS5Options { Host = "10.0.0.8", Description = "   ", Location = "" });
        var accepted = ConsoleSettings.Errors("ps5", new PS5Options
        {
            Host = "10.0.0.8",
            Description = new string('a', 120),
            Location = new string('b', 80),
        });
        var tooLong = ConsoleSettings.Errors("ps5", new PS5Options
        {
            Host = "10.0.0.8",
            Description = new string('a', 121),
            Location = new string('b', 81),
        });

        Assert.Empty(blank);
        Assert.Empty(accepted);
        Assert.Contains(tooLong, error => error.Contains("description must be at most 120", StringComparison.Ordinal));
        Assert.Contains(tooLong, error => error.Contains("location must be at most 80", StringComparison.Ordinal));
    }
}

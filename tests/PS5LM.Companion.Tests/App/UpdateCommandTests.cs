using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.App.Setup;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Infrastructure.Persistence;

namespace PS5LM.Companion.Tests.App;

public sealed class UpdateCommandTests
{
    [Fact]
    public void Repeated_hosts_are_kept_in_order()
    {
        var parsed = CommandSupport.ParseWithRepeated(
            ["--host", "10.0.0.8", "--settings", "--host", "10.0.0.9"],
            ["settings"],
            [],
            ["host"]);

        var targets = UpdateCommand.Targets(parsed, []);

        Assert.True(parsed.Has("settings"));
        Assert.Equal(["10.0.0.8", "10.0.0.9"], targets.Select(target => target.Host).ToArray());
        Assert.Equal(2121, targets[0].Options.FtpPort);
    }

    [Fact]
    public void All_consoles_uses_the_fleet_and_rejects_an_explicit_host()
    {
        var fleet = new List<(string Id, PS5Options Options)>
        {
            ("living-room", new PS5Options { Host = "10.0.0.8", FtpPort = 2122 }),
        };
        var all = CommandSupport.ParseWithRepeated(["--all-consoles"], ["all-consoles"], [], ["host"]);

        var targets = UpdateCommand.Targets(all, fleet);

        Assert.Equal("living-room", Assert.Single(targets).Label);
        Assert.Equal(2122, targets[0].Options.FtpPort);
        Assert.Throws<ArgumentException>(() => UpdateCommand.Targets(
            CommandSupport.ParseWithRepeated(["--all-consoles", "--host", "10.0.0.1"], ["all-consoles"], [], ["host"]),
            fleet));
        Assert.Throws<ArgumentException>(() => UpdateCommand.Targets(
            CommandSupport.ParseWithRepeated(["--all-consoles"], ["all-consoles"], [], ["host"]),
            []));
        Assert.Throws<ArgumentException>(() => UpdateCommand.Targets(
            CommandSupport.ParseWithRepeated([], [], [], ["host"]),
            fleet));
    }

    [Fact]
    public void An_explicit_ftp_port_overrides_the_fleet()
    {
        var fleet = new List<(string Id, PS5Options Options)> { ("den", new PS5Options { Host = "10.0.0.9", FtpPort = 2122 }) };
        var parsed = CommandSupport.ParseWithRepeated(["--all-consoles", "--ftp-port", "2140"], ["all-consoles"], ["ftp-port"], ["host"]);

        var target = Assert.Single(UpdateCommand.Targets(parsed, fleet));

        Assert.Equal(2140, target.Options.FtpPort);
    }

    [Theory]
    [InlineData(0, 3, 3)]
    [InlineData(3, 2, 2)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 1, 1)]
    [InlineData(0, 0, 0)]
    public void The_worst_exit_code_wins(int current, int next, int expected) =>
        Assert.Equal(expected, UpdateCommand.Worse(current, next));

    [Fact]
    public void The_fleet_is_configuration_then_stored_consoles()
    {
        var directory = Directory.CreateTempSubdirectory("fleet-consoles").FullName;
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PS5:Consoles:living-room:Host"] = "10.0.0.8",
                ["PS5:Consoles:living-room:FtpPort"] = "2122",
                ["PS5:Consoles:blank:Host"] = "  ",
            }).Build();
            new JsonConsoleStore(Path.Combine(directory, "consoles.json"), NullLogger<JsonConsoleStore>.Instance)
                .Save(
                [
                    new StoredConsole("living-room", new PS5Options { Host = "10.9.9.9" }),
                    new StoredConsole("den", new PS5Options { Host = "10.0.0.9" }),
                ]);

            var consoles = FleetConsoles.Load(configuration, directory);

            Assert.Equal(["living-room", "den"], consoles.Select(console => console.Id).ToArray());
            Assert.Equal("10.0.0.8", consoles[0].Options.Host);
            Assert.Equal(2122, consoles[0].Options.FtpPort);
            Assert.Equal("10.0.0.9", consoles[1].Options.Host);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

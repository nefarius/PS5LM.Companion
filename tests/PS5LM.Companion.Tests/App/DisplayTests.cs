using PS5LM.Companion.App.Components;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Tests.App;

public sealed class DisplayTests
{
    [Fact]
    public void Update_command_keeps_host_and_apps_on_cli_defaults()
    {
        Assert.Equal(
            "update --host 10.0.0.8 --apps",
            Display.UpdateCommand("10.0.0.8", new PS5Options { Host = "10.0.0.8" }, apps: true));
    }

    [Fact]
    public void Update_command_adds_payload_manager_url_and_ftp_port_when_they_differ()
    {
        var options = new PS5Options
        {
            Host = "10.0.0.8",
            PayloadManagerUrl = "http://10.0.0.8:9000/",
            FtpPort = 2122,
        };

        Assert.Equal(
            "update --host 10.0.0.8 --payload-manager-url http://10.0.0.8:9000/ --ftp-port 2122",
            Display.UpdateCommand("10.0.0.8", options, apps: false));
    }
}

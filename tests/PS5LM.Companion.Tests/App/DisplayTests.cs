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

    [Fact]
    public void Update_command_quotes_host_and_url_that_need_a_shell()
    {
        var options = new PS5Options
        {
            Host = "ps5.local",
            PayloadManagerUrl = "http://127.0.0.1:9000/api;a/",
        };

        Assert.Equal(
            "update --host ps5.local --payload-manager-url 'http://127.0.0.1:9000/api;a/'",
            Display.UpdateCommand("ps5.local", options, apps: false));
        Assert.Equal(
            "update --host ps5.local --payload-manager-url \"http://127.0.0.1:9000/api;a/\"",
            Display.TryUpdateCommand("ps5.local", options, apps: false, Display.CommandShell.Cmd));
        Assert.Equal(
            "update --host 'ps5 host'",
            Display.UpdateCommand("ps5 host", new PS5Options { Host = "ps5 host" }, apps: false));
        Assert.Equal(
            "update --host \"ps5 host\"",
            Display.TryUpdateCommand("ps5 host", new PS5Options { Host = "ps5 host" }, apps: false, Display.CommandShell.Cmd));
        Assert.Equal("'it'\\''s'", Display.QuoteForShell("it's"));
        Assert.Equal("\"it\"\"s\"", Display.QuoteForShell("it\"s", Display.CommandShell.Cmd));
        Assert.Null(Display.QuoteForShell("http://127.0.0.1/%2F", Display.CommandShell.Cmd));
        Assert.Null(Display.QuoteForShell("a\nb"));
        Assert.Equal(
            [
                new Display.ShellCommand(
                    "update --host ps5.local --payload-manager-url 'http://127.0.0.1:9000/api;a/'",
                    "POSIX"),
                new Display.ShellCommand(
                    "update --host ps5.local --payload-manager-url \"http://127.0.0.1:9000/api;a/\"",
                    "cmd.exe"),
            ],
            Display.UpdateCommandLines("ps5.local", options, apps: false));
        Assert.Equal(
            [new Display.ShellCommand("update --host 10.0.0.8", "POSIX or cmd.exe")],
            Display.UpdateCommandLines("10.0.0.8", new PS5Options { Host = "10.0.0.8" }, apps: false));
        Assert.Equal(
            "Run <code>update --host 10.0.0.8</code> (POSIX or cmd.exe) to bring them in line.",
            Display.UpdateCommandGuidance("10.0.0.8", new PS5Options { Host = "10.0.0.8" }, apps: false).Value);
    }
}

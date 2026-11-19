using MudBlazor;

using PS5LM.Companion.App.Components;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Tests.App;

public sealed class DisplayTests
{
    [Fact]
    public void Update_command_keeps_host_and_apps_on_cli_defaults()
    {
        Assert.Equal(
            "update --host 10.0.0.8 --apps",
            Display.UpdateCommand("10.0.0.8", new PS5Options { Host = "10.0.0.8" }, apps: true));
        Assert.Equal(
            "update --host 10.0.0.8 --apps --settings",
            Display.UpdateCommand("10.0.0.8", new PS5Options { Host = "10.0.0.8" }, apps: true, settings: true));
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

    [Fact]
    public void A_stopped_app_replaces_the_no_model_state()
    {
        var status = NoModel(Apps(
            new AppVariantStatus(PS5Variant.PS5LM, "PPSA99575", "PS5LM", true, AppRunState.NotRunning, true),
            new AppVariantStatus(PS5Variant.PS5LMPlus, "PPSA99581", "PS5LM Plus", false, AppRunState.Unknown, false)));

        Assert.Equal("App not running", Display.ConsoleState(status));
        Assert.Equal(Color.Error, Display.ConsoleStateColor(status));
    }

    [Theory]
    [InlineData(false, AppRunState.NotRunning)]
    [InlineData(true, AppRunState.Unknown)]
    [InlineData(true, AppRunState.RunningVariantUnknown)]
    public void An_uncertain_app_keeps_the_no_model_state(bool available, AppRunState state)
    {
        var status = NoModel(available
            ? Apps(new AppVariantStatus(PS5Variant.PS5LM, "PPSA99575", "PS5LM", true, state, state != AppRunState.Unknown))
            : AppControlStatus.NotChecked);

        Assert.Equal("No model loaded", Display.ConsoleState(status));
        Assert.Equal(Color.Warning, Display.ConsoleStateColor(status));
    }

    [Fact]
    public void No_installed_app_keeps_the_no_model_state()
    {
        var status = NoModel(Apps(
            new AppVariantStatus(PS5Variant.PS5LM, "PPSA99575", "PS5LM", false, AppRunState.NotRunning, false)));

        Assert.Equal("No model loaded", Display.ConsoleState(status));
        Assert.Equal(Color.Warning, Display.ConsoleStateColor(status));
    }

    [Fact]
    public void A_loaded_model_keeps_its_lifecycle_when_app_control_says_stopped()
    {
        var status = CompanionStatus.Initial with
        {
            Lifecycle = CompanionLifecycle.Ready,
            LoadedModel = "qwen.gguf",
            AppControl = Apps(new AppVariantStatus(PS5Variant.PS5LM, "PPSA99575", "PS5LM", true, AppRunState.NotRunning, true)),
        };

        Assert.Equal("Ready", Display.ConsoleState(status));
        Assert.Equal(Color.Success, Display.ConsoleStateColor(status));
    }

    private static CompanionStatus NoModel(AppControlStatus control) =>
        CompanionStatus.Initial with
        {
            Lifecycle = CompanionLifecycle.AppAvailableNoModel,
            AppControl = control,
        };

    private static AppControlStatus Apps(params AppVariantStatus[] apps) =>
        new(true, "VoidShell", "V1.1", "", apps);
}

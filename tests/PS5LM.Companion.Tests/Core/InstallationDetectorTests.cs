using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class InstallationDetectorTests
{
    private static Task<Installation> Detect(FakeFileSystem fs, PS5Variant configured = PS5Variant.Unknown) =>
        InstallationDetector.DetectAsync(fs, configured, CancellationToken.None);

    [Fact]
    public async Task Detects_PS5LM_by_its_title_folder()
    {
        var installation = await Detect(ConsoleFixtures.PS5LMInstalled());

        Assert.Equal(PS5Variant.PS5LM, installation.Profile?.Variant);
        Assert.Equal("/data/PS5LM", installation.StateRoot?.FtpPath);
    }

    [Fact]
    public async Task Detects_PS5LM_Plus_using_its_data_folder()
    {
        var installation = await Detect(ConsoleFixtures.PS5LMPlusInstalled());

        Assert.Equal(PS5Variant.PS5LMPlus, installation.Profile?.Variant);
        Assert.Equal("/data/PS5LM", installation.StateRoot?.FtpPath);
    }

    [Fact]
    public async Task PS5LM_Plus_falls_back_to_its_sandbox_when_data_is_closed()
    {
        var installation = await Detect(ConsoleFixtures.PS5LMPlusInstalled(sandboxed: true));

        Assert.Equal("/mnt/sandbox/PPSA99581_000/download0/PS5LM", installation.StateRoot?.FtpPath);
        Assert.Equal("/download0/PS5LM", installation.StateRoot?.AppPath);
    }

    [Fact]
    public async Task Nothing_installed_is_reported_without_a_profile()
    {
        var installation = await Detect(new FakeFileSystem());

        Assert.Null(installation.Profile);
        Assert.Contains("installed", installation.Message);
    }

    [Fact]
    public async Task Both_installed_is_ambiguous_until_configured()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddDirectory("/data/homebrew/PPSA99581");

        var ambiguous = await Detect(fs);
        var configured = await Detect(fs, PS5Variant.PS5LMPlus);

        Assert.Null(ambiguous.Profile);
        Assert.Contains("PS5:Variant", ambiguous.Message);
        Assert.Equal(PS5Variant.PS5LMPlus, configured.Profile?.Variant);
    }

    [Fact]
    public async Task Installed_app_without_state_asks_to_start_it_once()
    {
        var installation = await Detect(ConsoleFixtures.PS5LMInstalled(withState: false));

        Assert.NotNull(installation.Profile);
        Assert.Null(installation.StateRoot);
        Assert.Contains("start the app", installation.Message);
    }

    [Theory]
    [InlineData("01.009.001")]
    [InlineData("01.010.000")]
    public async Task An_older_PS5LM_ignores_a_stray_app_space_folder(string contentVersion)
    {
        var fs = new FakeFileSystem()
            .AddDirectory("/data/homebrew/PPSA99575")
            .AddFile(
                "/data/homebrew/PPSA99575/sce_sys/param.json",
                $$"""{"titleId":"PPSA99575","contentVersion":"{{contentVersion}}"}""")
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/app.log", "stray");

        var installation = await Detect(fs);

        Assert.Equal(PS5Variant.PS5LM, installation.Profile?.Variant);
        Assert.Null(installation.StateRoot);
        Assert.Contains("start the app", installation.Message);
    }

    [Fact]
    public async Task PS5LM_0_11_uses_app_space_when_that_is_where_its_state_is()
    {
        var installation = await Detect(ConsoleFixtures.PS5LMInstalled(contentVersion: "01.011.000", appStorage: true));

        Assert.Equal("/mnt/sandbox/PPSA99575_000/download0/PS5LM", installation.StateRoot?.FtpPath);
        Assert.Equal("/download0/PS5LM", installation.StateRoot?.AppPath);
        Assert.True(AppVersion.TryParseContentVersion("01.011.000", out var version));
        Assert.Equal(version, installation.AppVersion);
    }

    [Fact]
    public async Task PS5LM_0_11_with_state_in_both_roots_uses_the_newer_app_log()
    {
        var older = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var fs = ConsoleFixtures.PS5LMInstalled(contentVersion: "01.011.000")
            .AddFile("/data/PS5LM/app.log", "old", older)
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/app.log", "new", older.AddDays(1));

        var installation = await Detect(fs);

        Assert.Equal("/mnt/sandbox/PPSA99575_000/download0/PS5LM", installation.StateRoot?.FtpPath);
    }

    [Fact]
    public async Task PS5LM_0_11_keeps_data_when_the_logs_have_no_time()
    {
        var fs = ConsoleFixtures.PS5LMInstalled(contentVersion: "01.011.000")
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/settings.json", "{}");

        var installation = await Detect(fs);

        Assert.Equal("/data/PS5LM", installation.StateRoot?.FtpPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unreadable_param_json_still_finds_whichever_root_has_state(bool appStorage)
    {
        var fs = ConsoleFixtures.PS5LMInstalled(appStorage: appStorage)
            .AddFile("/data/homebrew/PPSA99575/sce_sys/param.json", "not json");

        var installation = await Detect(fs);

        Assert.Equal(
            appStorage ? "/mnt/sandbox/PPSA99575_000/download0/PS5LM" : "/data/PS5LM",
            installation.StateRoot?.FtpPath);
        Assert.Null(installation.AppVersion);
    }

    [Fact]
    public async Task Unreachable_console_propagates()
    {
        var fs = ConsoleFixtures.PS5LMInstalled();
        fs.Unreachable = true;

        await Assert.ThrowsAsync<ConsoleUnreachableException>(() => Detect(fs));
    }
}

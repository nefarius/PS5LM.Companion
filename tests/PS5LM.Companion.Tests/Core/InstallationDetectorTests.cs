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

    [Fact]
    public async Task Unreachable_console_propagates()
    {
        var fs = ConsoleFixtures.PS5LMInstalled();
        fs.Unreachable = true;

        await Assert.ThrowsAsync<ConsoleUnreachableException>(() => Detect(fs));
    }
}

using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Contracts;

/// <summary>ShadowMountPlus 1.7beta7's <c>debug.log</c> wording, which setup waits on.</summary>
public sealed class ShadowMountLogContractTests
{
    [Fact]
    public void A_fresh_install_counts_once_and_a_skipped_scan_does_not()
    {
        var log = FixtureFiles.Read("shadowmountplus", "1.7beta7", "debug.log");

        Assert.Contains("source not stable yet", log, StringComparison.Ordinal);
        Assert.Equal(1, ShadowMountLog.CountInstalled(log, "PPSA99575"));
        Assert.Equal(0, ShadowMountLog.CountInstalled(log, "PPSA99581"));
    }

    [Fact]
    public void A_line_already_in_the_log_is_not_a_new_install()
    {
        var log = FixtureFiles.Read("shadowmountplus", "1.7beta7", "debug.log");
        var baseline = ShadowMountLog.CountInstalled(log, "PPSA99575");

        Assert.False(ShadowMountLog.CountInstalled(log, "PPSA99575") > baseline);
        Assert.True(ShadowMountLog.CountInstalled(log + "NOTIFY: installed game PPSA99575\n", "PPSA99575") > baseline);
    }
}

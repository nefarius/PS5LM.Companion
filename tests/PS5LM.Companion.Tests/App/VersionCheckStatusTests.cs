using PS5LM.Companion.App.Updates;

namespace PS5LM.Companion.Tests.App;

public sealed class VersionCheckStatusTests
{
    [Fact]
    public void The_current_version_keeps_the_plain_chip()
    {
        var status = new VersionCheckStatus("1.0.1", null, false, null);

        Assert.True(status.HasLocalVersion);
        Assert.Equal("v1.0.1", status.ChipLabel);
        Assert.Equal("Version v1.0.1", status.AriaLabel);
        Assert.Equal("Running version v1.0.1.", status.Tooltip);
        Assert.Null(status.ReleaseUri);
    }

    [Fact]
    public void An_update_names_both_versions_and_keeps_the_release_link()
    {
        var release = new Uri("https://github.com/nefarius/PS5LM.Companion/releases/tag/v1.1.0");
        var status = new VersionCheckStatus("1.0.1", "v1.1.0", true, release);

        Assert.Equal("v1.0.1 · v1.1.0 available", status.ChipLabel);
        Assert.Equal("Version v1.0.1 · v1.1.0 available", status.AriaLabel);
        Assert.Equal("Running version v1.0.1. Version v1.1.0 is available.", status.Tooltip);
        Assert.Equal(release, status.ReleaseUri);
    }

    [Fact]
    public void A_missing_local_version_stays_unknown()
    {
        var status = new VersionCheckStatus("  ", null, false, null);

        Assert.False(status.HasLocalVersion);
        Assert.Equal("unknown", status.ChipLabel);
        Assert.Equal("Version unknown", status.AriaLabel);
        Assert.Equal("Version unavailable", status.Tooltip);
    }
}

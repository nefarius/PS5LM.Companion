using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;
using PS5LM.Companion.Core.Updates;

namespace PS5LM.Companion.Tests.Core;

public sealed class ComponentUpdatesTests
{
    private static readonly SetupManifest Manifest = SetupManifest.LoadEmbedded();

    [Fact]
    public void An_older_live_version_is_flagged()
    {
        var live = ComponentUpdates.Live(Manifest, "0.6.0", "1.0.0-nefarius.1");

        Assert.Contains(live, update => update.Id == PayloadDependencies.ManagerId && update.Pinned == Manifest.PayloadManager.Version);
        Assert.Contains(live, update => update.Id == PayloadDependencies.VoidShell && update.Pinned == Manifest.Payloads.Single(payload => payload.Id == PayloadDependencies.VoidShell).Version);
    }

    [Theory]
    [InlineData("0.6.3", "1.1.1-nefarius.4")]
    [InlineData("0.6.4", "2.0.0-nefarius.0")]
    [InlineData("v0.6.3", "v1.1.1-nefarius.4")]
    public void An_equal_or_newer_live_version_is_not_flagged(string manager, string voidShell)
    {
        Assert.Empty(ComponentUpdates.Live(Manifest, manager, voidShell));
    }

    [Fact]
    public void A_missing_semver_gives_no_live_verdict_for_voidshell()
    {
        var live = ComponentUpdates.Live(Manifest, Manifest.PayloadManager.Version, null);

        Assert.Empty(live);
    }

    [Fact]
    public void Hash_and_live_results_are_combined_without_duplicate_ids()
    {
        var live = new[] { new ComponentUpdate("voidshell", "VoidShell", "v1.1.1-nefarius.4", "1.0.0 -> v1.1.1-nefarius.4") };
        var hash = new ComponentUpdateSnapshot(
            [
                new ComponentUpdate("voidshell", "VoidShell", "v1.1.1-nefarius.4", "/data/voidshell/www/index.html differs from the pin"),
                new ComponentUpdate("kstuff-lite", "kstuff-lite", "v1.11", "stored file differs from v1.11"),
            ],
            DateTimeOffset.UnixEpoch,
            false,
            null);

        var merged = ComponentUpdates.Merge(live, hash);

        Assert.Equal(2, merged.Outdated.Count);
        var voidShell = Assert.Single(merged.Outdated, update => update.Id == "voidshell");
        Assert.Contains("1.0.0 ->", voidShell.Detail, StringComparison.Ordinal);
        Assert.Contains("index.html", voidShell.Detail, StringComparison.Ordinal);
        Assert.Contains(merged.Outdated, update => update.Id == "kstuff-lite");
        Assert.Equal(DateTimeOffset.UnixEpoch, merged.HashCheckedAt);
    }
}

using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.Tests.Support;

/// <summary>The kstuff invariants the harness records. Launch checks run from <see cref="SetupHarness.Dispose"/>.</summary>
internal static class KstuffSafety
{
    public static void AssertLaunches(SetupHarness harness)
    {
        Assert.DoesNotContain(harness.Events, e => e == "kstuff-conflict");
        if (!harness.Manifest.Payloads.Any(payload => payload.Id.Equals(BootSettings.KstuffPayloadId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        foreach (var load in harness.Loads)
        {
            var needsKstuff = load.File is "voidshell.elf" or "shadowmountplus.elf" or "ps5-web-file-manager.elf"
                || (load.File == "onionhen.elf" && !load.StartedBundled);
            Assert.False(needsKstuff && load.KstuffBefore < 1, $"{load.File} started while no kstuff was running.");
        }
    }

    public static void AssertBoot(SetupHarness harness, int exitCode)
    {
        AssertLaunches(harness);
        var boot = harness.SimulateBoot();
        Assert.False(boot.Conflict, "The next boot would load two kstuffs: " + harness.Autoload.List);
        Assert.True(boot.Kstuff <= 1, "The next boot would load " + boot.Kstuff + " kstuffs: " + harness.Autoload.List);
        if (exitCode == 0 && boot.Started.Any(id => id.Equals(BootSettings.OnionHenPayloadId, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Equal(1, boot.Kstuff);
        }
    }
}

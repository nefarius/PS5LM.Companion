namespace PS5LM.Companion.Tests.Support;

/// <summary>File layouts of the two apps as they appear over ftpsrv.</summary>
internal static class ConsoleFixtures
{
    /// <param name="contentVersion">Written into <c>sce_sys/param.json</c> when set, for example <c>01.011.000</c>.</param>
    /// <param name="appStorage">State lives in the title's own storage instead of <c>/data/PS5LM</c>.</param>
    public static FakeFileSystem PS5LMInstalled(bool withState = true, string? contentVersion = null, bool appStorage = false)
    {
        var fs = new FakeFileSystem().AddDirectory("/data/homebrew/PPSA99575");
        if (contentVersion is not null)
        {
            fs.AddFile(
                "/data/homebrew/PPSA99575/sce_sys/param.json",
                $$"""{"titleId":"PPSA99575","contentVersion":"{{contentVersion}}"}""");
        }

        if (withState)
        {
            var root = appStorage ? "/mnt/sandbox/PPSA99575_000/download0/PS5LM" : "/data/PS5LM";
            fs.AddFile($"{root}/settings.json", "{}").AddFile($"{root}/app.log", "log");
        }

        return fs;
    }

    public static FakeFileSystem PS5LMPlusInstalled(bool sandboxed = false)
    {
        var fs = new FakeFileSystem().AddDirectory("/data/homebrew/PPSA99581");
        return sandboxed
            ? fs.AddFile("/mnt/sandbox/PPSA99581_000/download0/PS5LM/app.log", "log")
            : fs.AddFile("/data/PS5LM/settings.json", "{}");
    }
}

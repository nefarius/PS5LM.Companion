namespace PS5LM.Companion.Tests.Support;

/// <summary>File layouts of the two apps as they appear over ftpsrv.</summary>
internal static class ConsoleFixtures
{
    public static FakeFileSystem PS5LMInstalled(bool withState = true)
    {
        var fs = new FakeFileSystem().AddDirectory("/data/homebrew/PPSA99575");
        if (withState)
        {
            fs.AddFile("/data/PS5LM/settings.json", "{}").AddFile("/data/PS5LM/app.log", "log");
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

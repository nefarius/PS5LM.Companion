using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Tests.Core;

public sealed class StorageRootTests
{
    private static readonly StorageRoot Sandbox =
        new("App space", "/mnt/sandbox/PPSA99581_000/download0/PS5LM", "/download0/PS5LM", Writable: true);

    [Fact]
    public void Maps_the_apps_view_to_the_ftp_view_and_back()
    {
        Assert.Equal(
            "/mnt/sandbox/PPSA99581_000/download0/PS5LM/models/a.gguf",
            Sandbox.ToFtpPath("/download0/PS5LM/models/a.gguf"));
        Assert.Equal(
            "/download0/PS5LM/models/a.gguf",
            Sandbox.ToAppPath("/mnt/sandbox/PPSA99581_000/download0/PS5LM/models/a.gguf"));
    }

    [Theory]
    [InlineData("/download0/PS5LMother/x.gguf")]
    [InlineData("/data/PS5LM/models/x.gguf")]
    [InlineData("/download0")]
    public void Paths_outside_the_root_do_not_map(string path) => Assert.Null(Sandbox.ToFtpPath(path));
}

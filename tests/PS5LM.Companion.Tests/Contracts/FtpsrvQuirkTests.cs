using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Infrastructure.Ftp;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Contracts;

/// <summary><see cref="FtpFileSystem"/> against the recorded ftpsrv replies.</summary>
public sealed class FtpsrvQuirkTests
{
    [Fact]
    public async Task Listing_uses_the_working_directory_and_drops_dot_entries()
    {
        await using var server = FtpsrvStandIn.Start();
        using var fs = new FtpFileSystem(new PS5Options { Host = "127.0.0.1", FtpPort = server.Port }, NullLogger<FtpFileSystem>.Instance);

        var shadow = await fs.ListAsync("/data/shadowmount", CancellationToken.None);
        var empty = await fs.ListAsync("/data/OnionHEN/games", CancellationToken.None);

        Assert.Contains(shadow, entry => entry.Name == "config.ini" && !entry.IsDirectory);
        Assert.Contains(shadow, entry => entry.Name == "debug.log");
        Assert.DoesNotContain(shadow, entry => entry.Name is "." or "..");
        Assert.Empty(empty);
    }

    [Fact]
    public async Task Deleting_a_file_accepts_the_226_reply()
    {
        await using var server = FtpsrvStandIn.Start();
        using var fs = new FtpFileSystem(new PS5Options { Host = "127.0.0.1", FtpPort = server.Port }, NullLogger<FtpFileSystem>.Instance);

        await fs.DeleteAsync("/data/shadowmount/debug.log", CancellationToken.None);
    }
}

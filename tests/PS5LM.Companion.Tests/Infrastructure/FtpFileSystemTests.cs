using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Infrastructure.Ftp;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Infrastructure;

/// <summary>
/// <see cref="FtpFileSystem"/> against a real FTP server. ftpsrv on the console is a different implementation, so
/// these prove the client logic (paths, .part upload and rename, timeouts) but not ftpsrv quirks.
/// </summary>
public sealed class FtpFileSystemTests : IAsyncLifetime
{
    private FtpServerFixture _server = null!;
    private FtpFileSystem _fs = null!;

    public async Task InitializeAsync()
    {
        _server = await FtpServerFixture.StartAsync();
        _fs = new FtpFileSystem(new PS5Options { Host = "127.0.0.1", FtpPort = _server.Port }, NullLogger<FtpFileSystem>.Instance);
    }

    public async Task DisposeAsync()
    {
        _fs.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Reports_existing_and_missing_files_and_folders()
    {
        Directory.CreateDirectory(_server.Local("/data/homebrew/PPSA99575"));
        Directory.CreateDirectory(_server.Local("/data/PS5LM"));
        await File.WriteAllTextAsync(_server.Local("/data/PS5LM/app.log"), "x");

        Assert.True(await _fs.DirectoryExistsAsync("/data/homebrew/PPSA99575", CancellationToken.None));
        Assert.False(await _fs.DirectoryExistsAsync("/data/homebrew/PPSA99581", CancellationToken.None));
        Assert.True(await _fs.FileExistsAsync("/data/PS5LM/app.log", CancellationToken.None));
        Assert.False(await _fs.FileExistsAsync("/data/PS5LM/settings.json", CancellationToken.None));
    }

    [Fact]
    public async Task Lists_files_with_sizes_and_folders_and_an_empty_list_for_a_missing_folder()
    {
        Directory.CreateDirectory(_server.Local("/data/PS5LM/models/sub"));
        await File.WriteAllBytesAsync(_server.Local("/data/PS5LM/models/a.gguf"), new byte[1234]);

        var entries = await _fs.ListAsync("/data/PS5LM/models", CancellationToken.None);
        var missing = await _fs.ListAsync("/data/PS5LM/nothing", CancellationToken.None);

        var file = Assert.Single(entries, e => !e.IsDirectory);
        Assert.Equal("a.gguf", file.Name);
        Assert.Equal(1234, file.Size);
        Assert.Contains(entries, e => e is { IsDirectory: true, Name: "sub" });
        Assert.Empty(missing);
    }

    [Fact]
    public async Task Writes_and_reads_text_and_refuses_files_over_the_limit()
    {
        await _fs.WriteTextAsync("/data/PS5LM/load", "/data/PS5LM/models/a.gguf\n", CancellationToken.None);

        Assert.Equal("/data/PS5LM/models/a.gguf\n", await File.ReadAllTextAsync(_server.Local("/data/PS5LM/load")));
        Assert.Equal("/data/PS5LM/models/a.gguf\n", await _fs.ReadTextAsync("/data/PS5LM/load", 1024, CancellationToken.None));
        Assert.Null(await _fs.ReadTextAsync("/data/PS5LM/load", 5, CancellationToken.None));
        Assert.Null(await _fs.ReadTextAsync("/data/PS5LM/missing", 1024, CancellationToken.None));
    }

    /// <summary>Records progress synchronously, so nothing is lost to a posted callback.</summary>
    private sealed class SyncProgress : IProgress<long>
    {
        public List<long> Reports { get; } = [];

        public void Report(long value) => Reports.Add(value);
    }

    [Fact]
    public async Task Uploads_as_a_part_file_reports_progress_and_renames_into_place()
    {
        var content = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
        var progress = new SyncProgress();
        await using var stream = new MemoryStream(content);

        await _fs.UploadAsync(stream, "/data/PS5LM/models/m.gguf.part", progress, CancellationToken.None);
        Assert.NotEmpty(progress.Reports);
        Assert.Equal(content.LongLength, progress.Reports[^1]);
        Assert.True(File.Exists(_server.Local("/data/PS5LM/models/m.gguf.part")));
        Assert.False(File.Exists(_server.Local("/data/PS5LM/models/m.gguf")));

        await _fs.MoveAsync("/data/PS5LM/models/m.gguf.part", "/data/PS5LM/models/m.gguf", CancellationToken.None);

        Assert.Equal(content, await File.ReadAllBytesAsync(_server.Local("/data/PS5LM/models/m.gguf")));
        Assert.False(File.Exists(_server.Local("/data/PS5LM/models/m.gguf.part")));
    }

    [Fact]
    public async Task Delete_removes_a_file_and_ignores_a_missing_one()
    {
        await _fs.WriteTextAsync("/data/PS5LM/unload", "-\n", CancellationToken.None);

        await _fs.DeleteAsync("/data/PS5LM/unload", CancellationToken.None);
        await _fs.DeleteAsync("/data/PS5LM/unload", CancellationToken.None);

        Assert.False(File.Exists(_server.Local("/data/PS5LM/unload")));
    }

    [Fact]
    public async Task A_stopped_server_is_reported_as_an_unreachable_console()
    {
        var dead = new FtpFileSystem(new PS5Options { Host = "127.0.0.1", FtpPort = 1 }, NullLogger<FtpFileSystem>.Instance);

        await Assert.ThrowsAsync<ConsoleUnreachableException>(() => dead.DirectoryExistsAsync("/data", CancellationToken.None));
        dead.Dispose();
    }
}

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ModelCatalogTests
{
    private static async Task<Installation> DetectAsync(FakeFileSystem fs) =>
        await InstallationDetector.DetectAsync(fs, PS5Variant.Unknown, CancellationToken.None);

    [Fact]
    public async Task Lists_only_gguf_files_with_the_apps_path_and_flags_the_loaded_one()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/b.gguf", new byte[10])
            .AddFile("/data/PS5LM/models/a.gguf", new byte[20])
            .AddFile("/data/PS5LM/models/notes.txt", "x")
            .AddFile("/data/PS5LM/models/mmproj-a.gguf", new byte[5])
            .AddFile("/data/PS5LM/models/a.gguf.part", new byte[5]);

        var models = await new ModelCatalog(fs).ListAsync(await DetectAsync(fs), "/data/PS5LM/models/b.gguf", CancellationToken.None);

        Assert.Equal(["a.gguf", "b.gguf", "mmproj-a.gguf"], models.Select(m => m.FileName));
        Assert.Equal("/data/PS5LM/models/a.gguf", models[0].AppPath);
        Assert.Equal(20, models[0].SizeBytes);
        Assert.True(models.Single(m => m.FileName == "b.gguf").Loaded);
        Assert.False(models.Single(m => m.FileName == "a.gguf").Loaded);
        Assert.True(models[^1].IsProjector);
    }

    [Fact]
    public async Task Sandboxed_PS5LM_Plus_reports_paths_as_the_app_sees_them()
    {
        var fs = ConsoleFixtures.PS5LMPlusInstalled(sandboxed: true)
            .AddFile("/mnt/sandbox/PPSA99581_000/download0/PS5LM/models/a.gguf", new byte[1])
            .AddFile("/data/homebrew/PPSA99581/models/bundled.gguf", new byte[1]);

        var models = await new ModelCatalog(fs).ListAsync(await DetectAsync(fs), null, CancellationToken.None);

        Assert.Contains(models, m => m.AppPath == "/download0/PS5LM/models/a.gguf");
        Assert.Contains(models, m => m.AppPath == "/app0/models/bundled.gguf");
    }

    [Fact]
    public async Task Upload_targets_include_a_removable_drive_only_when_it_is_mounted()
    {
        var fs = ConsoleFixtures.PS5LMInstalled();
        var catalog = new ModelCatalog(fs);
        var installation = await DetectAsync(fs);

        var before = await catalog.UploadTargetsAsync(installation, CancellationToken.None);
        fs.AddDirectory("/mnt/usb0");
        var after = await catalog.UploadTargetsAsync(installation, CancellationToken.None);

        Assert.Equal(["Internal storage"], before.Select(t => t.Name));
        Assert.Equal(["Internal storage", "USB 0"], after.Select(t => t.Name));
    }

    [Fact]
    public async Task The_read_only_app_folder_is_never_an_upload_target()
    {
        var fs = ConsoleFixtures.PS5LMPlusInstalled().AddFile("/data/homebrew/PPSA99581/models/bundled.gguf", new byte[1]);

        var targets = await new ModelCatalog(fs).UploadTargetsAsync(await DetectAsync(fs), CancellationToken.None);

        Assert.DoesNotContain(targets, t => t.Name == "App folder");
    }
}

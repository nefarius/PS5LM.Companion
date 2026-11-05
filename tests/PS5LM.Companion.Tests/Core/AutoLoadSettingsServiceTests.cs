using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class AutoLoadSettingsServiceTests
{
    private const string Model = "/data/PS5LM/models/a.gguf";

    private readonly FakeTimeProvider _time = new();
    private readonly FakeLlamaProbe _llama = new();
    private readonly LoadTracker _tracker;
    private readonly CompanionStatusStore _store = new();

    public AutoLoadSettingsServiceTests() => _tracker = new LoadTracker(_time);

    [Fact]
    public async Task A_missing_file_reads_as_the_app_defaults_and_is_created_with_only_the_two_keys()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile(Model, new byte[1]);
        await fs.DeleteAsync("/data/PS5LM/settings.json", CancellationToken.None);
        var service = Create(fs);

        var read = await service.GetAsync(CancellationToken.None);
        var unchanged = await service.SetAsync(false, null, CancellationToken.None);

        Assert.True(read.Succeeded);
        Assert.False(read.Settings!.AutoLoad);
        Assert.Null(read.Settings.DefaultModel);
        Assert.True(read.Settings.DefaultModelInLibrary);
        Assert.Equal(OperationOutcome.NoChange, unchanged.Outcome);
        Assert.False(fs.Files.ContainsKey("/data/PS5LM/settings.json"));

        var created = await service.SetAsync(true, Model, CancellationToken.None);
        Assert.Equal(OperationOutcome.Completed, created.Outcome);
        Assert.Equal("Applies the next time the app starts.", created.Message);
        var written = JsonNode.Parse(fs.Text("/data/PS5LM/settings.json")!)!.AsObject();
        Assert.Equal(2, written.Count);
        Assert.True(written["auto_load"]!.GetValue<bool>());
        Assert.Equal(Model, written["default_model"]!.GetValue<string>());
        Assert.EndsWith("\n", fs.Text("/data/PS5LM/settings.json"));
    }

    [Fact]
    public async Task Other_keys_survive_a_write_and_a_wrong_type_is_replaced()
    {
        const string original = """
            {
              "auto_load": "yes",
              "default_model": 1,
              "ctx_cap": 8192,
              "kv_type": "q4_0",
              "sounds": false,
              "extra": { "keep": true },
              "note": "leave me"
            }
            """;
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/settings.json", original).AddFile(Model, new byte[1]);
        var service = Create(fs);

        var read = await service.GetAsync(CancellationToken.None);
        Assert.Equal(OperationOutcome.Invalid, read.Outcome);
        Assert.Null(read.Settings);
        Assert.Equal(original, fs.Text("/data/PS5LM/settings.json"));
        Assert.Empty(fs.Writes);

        var written = await service.SetAsync(true, Model, CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, written.Outcome);
        var json = JsonNode.Parse(fs.Text("/data/PS5LM/settings.json")!)!.AsObject();
        Assert.True(json["auto_load"]!.GetValue<bool>());
        Assert.Equal(Model, json["default_model"]!.GetValue<string>());
        Assert.Equal(8192, json["ctx_cap"]!.GetValue<int>());
        Assert.Equal("q4_0", json["kv_type"]!.GetValue<string>());
        Assert.False(json["sounds"]!.GetValue<bool>());
        Assert.True(json["extra"]!["keep"]!.GetValue<bool>());
        Assert.Equal("leave me", json["note"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("")]
    public async Task A_file_that_is_not_an_object_is_left_unchanged(string content)
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/settings.json", content).AddFile(Model, new byte[1]);

        var read = await Create(fs).GetAsync(CancellationToken.None);
        var write = await Create(fs).SetAsync(true, Model, CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, read.Outcome);
        Assert.Equal(OperationOutcome.Invalid, write.Outcome);
        Assert.Equal(content, fs.Text("/data/PS5LM/settings.json"));
        Assert.Empty(fs.Writes);
        Assert.Empty(fs.Moves);
    }

    [Fact]
    public async Task An_unknown_path_and_a_projector_are_not_accepted()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile(Model, new byte[1])
            .AddFile("/data/PS5LM/models/mmproj-a.gguf", new byte[1]);
        var service = Create(fs);

        var missing = await service.SetAsync(true, "/data/PS5LM/models/missing.gguf", CancellationToken.None);
        var escape = await service.SetAsync(true, "/data/PS5LM/models/../settings.json", CancellationToken.None);
        var projector = await service.SetAsync(false, "/data/PS5LM/models/mmproj-a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.NotFound, missing.Outcome);
        Assert.Equal(OperationOutcome.NotFound, escape.Outcome);
        Assert.Equal(OperationOutcome.NotFound, projector.Outcome);
        Assert.Contains("projector", projector.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{}", fs.Text("/data/PS5LM/settings.json"));
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task Turning_auto_load_off_keeps_a_stored_model_that_is_not_on_the_console()
    {
        const string gone = "/data/PS5LM/models/gone.gguf";
        const string original = """{"auto_load":true,"default_model":"/data/PS5LM/models/gone.gguf","sounds":false}""";
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/settings.json", original);
        var service = Create(fs);

        var turnedOff = await service.SetAsync(false, gone, CancellationToken.None);
        var turnedOn = await service.SetAsync(true, gone, CancellationToken.None);
        var other = await service.SetAsync(false, "/data/PS5LM/models/other.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, turnedOff.Outcome);
        var json = JsonNode.Parse(fs.Text("/data/PS5LM/settings.json")!)!;
        Assert.False(json["auto_load"]!.GetValue<bool>());
        Assert.Equal(gone, json["default_model"]!.GetValue<string>());
        Assert.False(json["sounds"]!.GetValue<bool>());
        Assert.Equal(OperationOutcome.NotFound, turnedOn.Outcome);
        Assert.Equal(OperationOutcome.NotFound, other.Outcome);
        Assert.Equal(gone, JsonNode.Parse(fs.Text("/data/PS5LM/settings.json")!)!["default_model"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_write_that_would_exceed_the_read_limit_is_refused()
    {
        var pad = new string('x', AutoLoadSettingsService.MaxSettingsBytes - 32);
        var original = "{\"pad\":\"" + pad + "\"}";
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(original), 1, AutoLoadSettingsService.MaxSettingsBytes);
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/settings.json", original).AddFile(Model, new byte[1]);

        var result = await Create(fs).SetAsync(true, Model, CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, result.Outcome);
        Assert.Equal(original, fs.Text("/data/PS5LM/settings.json"));
        Assert.Empty(fs.Writes);
        Assert.Empty(fs.Moves);
    }

    [Fact]
    public async Task Turning_auto_load_on_without_a_model_is_refused_and_clearing_the_model_turns_it_off()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile(Model, new byte[1]);
        var service = Create(fs);

        var refused = await service.SetAsync(true, null, CancellationToken.None);
        var blank = await service.SetAsync(true, string.Empty, CancellationToken.None);
        Assert.Equal(OperationOutcome.Invalid, refused.Outcome);
        Assert.Equal(OperationOutcome.Invalid, blank.Outcome);
        Assert.Equal("{}", fs.Text("/data/PS5LM/settings.json"));

        Assert.Equal(OperationOutcome.Completed, (await service.SetAsync(true, Model, CancellationToken.None)).Outcome);
        var cleared = await service.SetAsync(false, null, CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, cleared.Outcome);
        var json = JsonNode.Parse(fs.Text("/data/PS5LM/settings.json")!)!;
        Assert.False(json["auto_load"]!.GetValue<bool>());
        Assert.Equal(string.Empty, json["default_model"]!.GetValue<string>());
        var read = await service.GetAsync(CancellationToken.None);
        Assert.False(read.Settings!.AutoLoad);
        Assert.Null(read.Settings.DefaultModel);
    }

    [Fact]
    public async Task An_unchanged_value_is_not_rewritten()
    {
        const string original = """
            {
              "auto_load": true,
              "default_model": "/data/PS5LM/models/a.gguf",
              "ctx_cap": 65536
            }
            """;
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/settings.json", original).AddFile(Model, new byte[1]);

        var result = await Create(fs).SetAsync(true, Model, CancellationToken.None);

        Assert.Equal(OperationOutcome.NoChange, result.Outcome);
        Assert.Equal(original, fs.Text("/data/PS5LM/settings.json"));
        Assert.Empty(fs.Writes);
        Assert.Empty(fs.Moves);
    }

    [Fact]
    public async Task The_write_goes_through_a_temporary_file_and_a_move()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile(Model, new byte[1]);

        var result = await Create(fs).SetAsync(true, Model, CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.Equal(["/data/PS5LM/settings.json.tmp"], fs.Writes);
        Assert.Equal([("/data/PS5LM/settings.json.tmp", "/data/PS5LM/settings.json")], fs.Moves);
        Assert.False(fs.Files.ContainsKey("/data/PS5LM/settings.json.tmp"));
        Assert.Contains("\"auto_load\": true", fs.Text("/data/PS5LM/settings.json"));
    }

    [Fact]
    public async Task A_stored_model_that_is_not_on_the_console_is_reported()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/settings.json", """{"auto_load":true,"default_model":"/data/PS5LM/models/gone.gguf"}""");

        var read = await Create(fs).GetAsync(CancellationToken.None);

        Assert.Equal("/data/PS5LM/models/gone.gguf", read.Settings!.DefaultModel);
        Assert.True(read.Settings.AutoLoad);
        Assert.False(read.Settings.DefaultModelInLibrary);
    }

    [Fact]
    public async Task No_state_folder_is_unavailable()
    {
        var fs = ConsoleFixtures.PS5LMInstalled(withState: false);

        var read = await Create(fs).GetAsync(CancellationToken.None);
        var write = await Create(fs).SetAsync(false, null, CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, read.Outcome);
        Assert.Equal(OperationOutcome.Unavailable, write.Outcome);
    }

    [Fact]
    public async Task PS5LM_0_9_1_writes_data_and_leaves_the_sandbox_alone()
    {
        var fs = ConsoleFixtures.PS5LMInstalled(contentVersion: "01.009.001")
            .AddFile(Model, new byte[1])
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/settings.json", "sandbox\n")
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/models/a.gguf", new byte[1]);

        await Writes(fs, "/data/PS5LM", Model);

        Assert.Equal("sandbox\n", fs.Text("/mnt/sandbox/PPSA99575_000/download0/PS5LM/settings.json"));
    }

    [Fact]
    public async Task PS5LM_0_11_0_in_app_storage_writes_the_sandbox_with_the_app_path()
    {
        var fs = ConsoleFixtures.PS5LMInstalled(contentVersion: "01.011.000", appStorage: true)
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/models/a.gguf", new byte[1]);

        await Writes(fs, "/mnt/sandbox/PPSA99575_000/download0/PS5LM", "/download0/PS5LM/models/a.gguf");
    }

    [Fact]
    public async Task PS5LM_Plus_in_its_sandbox_writes_there()
    {
        var fs = ConsoleFixtures.PS5LMPlusInstalled(sandboxed: true)
            .AddFile("/mnt/sandbox/PPSA99581_000/download0/PS5LM/models/a.gguf", new byte[1]);

        await Writes(fs, "/mnt/sandbox/PPSA99581_000/download0/PS5LM", "/download0/PS5LM/models/a.gguf");
    }

    [Fact]
    public async Task An_unreadable_version_uses_the_sandbox_when_that_is_the_only_state_and_data_when_they_tie()
    {
        var sandboxOnly = new FakeFileSystem()
            .AddDirectory("/data/homebrew/PPSA99575")
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/app.log", "log")
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/models/a.gguf", new byte[1]);
        await Writes(sandboxOnly, "/mnt/sandbox/PPSA99575_000/download0/PS5LM", "/download0/PS5LM/models/a.gguf");

        var both = ConsoleFixtures.PS5LMInstalled()
            .AddFile(Model, new byte[1])
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/settings.json", "sandbox\n")
            .AddFile("/mnt/sandbox/PPSA99575_000/download0/PS5LM/app.log", "log");
        await Writes(both, "/data/PS5LM", Model);
        Assert.Equal("sandbox\n", both.Text("/mnt/sandbox/PPSA99575_000/download0/PS5LM/settings.json"));
    }

    private async Task Writes(FakeFileSystem fs, string ftpRoot, string appPath)
    {
        var result = await Create(fs).SetAsync(true, appPath, CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        var json = JsonNode.Parse(fs.Text($"{ftpRoot}/{VariantProfile.SettingsFile}")!)!;
        Assert.True(json["auto_load"]!.GetValue<bool>());
        Assert.Equal(appPath, json["default_model"]!.GetValue<string>());
        Assert.False(fs.Files.ContainsKey($"{ftpRoot}/{VariantProfile.SettingsFile}.tmp"));
    }

    private AutoLoadSettingsService Create(FakeFileSystem fs)
    {
        var status = new CompanionStatusService(
            new PS5Options { Host = "ps5" },
            fs,
            _llama,
            new FakeSensorProbe(),
            new FakePayloadCatalog(),
            new FakeElfldrProbe(),
            _tracker,
            _time);
        return new AutoLoadSettingsService(fs, new ModelCatalog(fs), status, _store);
    }
}

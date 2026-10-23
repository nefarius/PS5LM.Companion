using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ModelControlServiceTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly LoadTracker _tracker;
    private readonly CompanionStatusStore _store = new();

    public ModelControlServiceTests() => _tracker = new LoadTracker(_time);

    private ModelControlService Create(FakeFileSystem fs)
    {
        var status = new CompanionStatusService(new PS5Options { Host = "ps5" }, fs, new FakeLlamaProbe(), new FakeSensorProbe(), _tracker, _time);
        return new ModelControlService(fs, new ModelCatalog(fs), status, _store, _tracker);
    }

    [Fact]
    public async Task Load_writes_the_apps_path_to_the_load_command_file_and_marks_a_pending_load()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);

        var result = await Create(fs).LoadAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Equal("/data/PS5LM/models/a.gguf\n", fs.Text("/data/PS5LM/load"));
        Assert.True(_tracker.IsLoading(TimeSpan.FromMinutes(1)));
        Assert.Equal("/data/PS5LM/models/a.gguf", _tracker.RequestedModel);
    }

    [Fact]
    public async Task Sandboxed_PS5LM_Plus_gets_the_command_file_over_ftp_and_the_app_path_inside_it()
    {
        var fs = ConsoleFixtures.PS5LMPlusInstalled(sandboxed: true)
            .AddFile("/mnt/sandbox/PPSA99581_000/download0/PS5LM/models/a.gguf", new byte[1]);

        var result = await Create(fs).LoadAsync("/download0/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("/download0/PS5LM/models/a.gguf\n", fs.Text("/mnt/sandbox/PPSA99581_000/download0/PS5LM/load"));
    }

    [Theory]
    [InlineData("/data/PS5LM/models/missing.gguf")]
    [InlineData("/data/PS5LM/models/../settings.json")]
    [InlineData("/etc/passwd")]
    [InlineData("/data/PS5LM/models/a.gguf\n/etc/passwd")]
    public async Task Load_only_accepts_paths_that_are_models_on_the_console(string path)
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);

        var result = await Create(fs).LoadAsync(path, CancellationToken.None);

        Assert.Equal(OperationOutcome.NotFound, result.Outcome);
        Assert.Null(fs.Text("/data/PS5LM/load"));
    }

    [Fact]
    public async Task A_projector_cannot_be_loaded()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/mmproj-a.gguf", new byte[1]);

        var result = await Create(fs).LoadAsync("/data/PS5LM/models/mmproj-a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task A_second_command_is_refused_until_the_first_was_consumed()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/a.gguf", new byte[1])
            .AddFile("/data/PS5LM/unload", "-\n");

        var result = await Create(fs).LoadAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, result.Outcome);
        Assert.Equal("-\n", fs.Text("/data/PS5LM/unload"));
    }

    [Fact]
    public async Task Loading_the_already_loaded_model_is_a_no_op()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);
        _store.Publish(CompanionStatus.Initial with { LoadedModel = "a.gguf" });

        var result = await Create(fs).LoadAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.NoChange, result.Outcome);
        Assert.Null(fs.Text("/data/PS5LM/load"));
    }

    [Fact]
    public async Task A_pending_unload_outranks_the_already_loaded_no_op()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/a.gguf", new byte[1])
            .AddFile("/data/PS5LM/unload", "-\n");
        _store.Publish(CompanionStatus.Initial with { LoadedModel = "/data/PS5LM/models/a.gguf" });

        var result = await Create(fs).LoadAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task Concurrent_commands_cannot_both_be_accepted()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);
        var control = Create(fs);

        var results = await Task.WhenAll(
            control.LoadAsync("/data/PS5LM/models/a.gguf", CancellationToken.None),
            control.UnloadAsync(CancellationToken.None),
            control.LoadAsync("/data/PS5LM/models/a.gguf", CancellationToken.None),
            control.UnloadAsync(CancellationToken.None));

        Assert.Single(results, r => r.Outcome == OperationOutcome.Accepted);
        Assert.Equal(3, results.Count(r => r.Outcome == OperationOutcome.Conflict));
    }

    [Fact]
    public async Task Unload_writes_a_dash_because_an_empty_file_would_be_ignored_and_clears_the_pending_load()
    {
        var fs = ConsoleFixtures.PS5LMInstalled();
        _tracker.MarkRequested("x");

        var result = await Create(fs).UnloadAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Equal("-\n", fs.Text("/data/PS5LM/unload"));
        Assert.False(_tracker.IsLoading(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Commands_are_unavailable_without_a_known_state_folder()
    {
        var fs = ConsoleFixtures.PS5LMInstalled(withState: false);

        var load = await Create(fs).LoadAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);
        var unload = await Create(fs).UnloadAsync(CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, load.Outcome);
        Assert.Equal(OperationOutcome.Unavailable, unload.Outcome);
    }

    [Fact]
    public async Task A_blank_path_is_invalid() =>
        Assert.Equal(OperationOutcome.Invalid, (await Create(ConsoleFixtures.PS5LMInstalled()).LoadAsync(" ", CancellationToken.None)).Outcome);
}

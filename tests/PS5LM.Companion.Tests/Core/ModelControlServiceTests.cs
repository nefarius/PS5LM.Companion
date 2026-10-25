using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class ModelControlServiceTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeLlamaProbe _llama = new();
    private readonly LoadTracker _tracker;
    private readonly CompanionStatusStore _store = new();

    public ModelControlServiceTests() => _tracker = new LoadTracker(_time);

    private ModelControlService Create(FakeFileSystem fs)
    {
        var status = new CompanionStatusService(new PS5Options { Host = "ps5" }, fs, _llama, new FakeSensorProbe(), new FakePayloadCatalog(), new FakeElfldrProbe(), _tracker, _time);
        return new ModelControlService(fs, new ModelCatalog(fs), status, _store, _tracker, TimeSpan.FromSeconds(600));
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

    [Fact]
    public async Task Delete_removes_only_the_selected_file()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/d1-omni-600M-BF16.gguf", new byte[4])
            .AddFile("/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf", new byte[2]);

        var result = await Create(fs).DeleteAsync("/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.Contains("Restart the PS5 app", result.Message);
        Assert.False(fs.Files.ContainsKey("/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf"));
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/d1-omni-600M-BF16.gguf"));
    }

    [Fact]
    public async Task Delete_of_a_main_model_leaves_its_projector()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/d1-omni-600M-BF16.gguf", new byte[4])
            .AddFile("/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf", new byte[2]);

        var result = await Create(fs).DeleteAsync("/data/PS5LM/models/d1-omni-600M-BF16.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.False(fs.Files.ContainsKey("/data/PS5LM/models/d1-omni-600M-BF16.gguf"));
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/mmproj-d1-omni-600M-BF16.gguf"));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("/data/PS5LM/models/missing.gguf")]
    [InlineData("/data/PS5LM/models/../settings.json")]
    [InlineData("/etc/passwd")]
    public async Task Delete_only_accepts_a_catalogued_model_path(string path)
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);

        var result = await Create(fs).DeleteAsync(path, CancellationToken.None);

        Assert.Equal(string.IsNullOrWhiteSpace(path) ? OperationOutcome.Invalid : OperationOutcome.NotFound, result.Outcome);
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
    }

    [Fact]
    public async Task Delete_refuses_the_read_only_app_folder()
    {
        var fs = ConsoleFixtures.PS5LMPlusInstalled().AddFile("/data/homebrew/PPSA99581/models/bundled.gguf", new byte[1]);

        var result = await Create(fs).DeleteAsync("/app0/models/bundled.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Invalid, result.Outcome);
        Assert.True(fs.Files.ContainsKey("/data/homebrew/PPSA99581/models/bundled.gguf"));
    }

    [Fact]
    public async Task Delete_refuses_the_loaded_model_and_a_projector_while_any_model_is_loaded()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/a.gguf", new byte[1])
            .AddFile("/data/PS5LM/models/b.gguf", new byte[1])
            .AddFile("/data/PS5LM/models/mmproj-a.gguf", new byte[1]);
        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, null, "/data/PS5LM/models/a.gguf");
        var control = Create(fs);

        var loaded = await control.DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);
        var projector = await control.DeleteAsync("/data/PS5LM/models/mmproj-a.gguf", CancellationToken.None);
        var other = await control.DeleteAsync("/data/PS5LM/models/b.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, loaded.Outcome);
        Assert.Equal(OperationOutcome.Conflict, projector.Outcome);
        Assert.Equal(OperationOutcome.Completed, other.Outcome);
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/mmproj-a.gguf"));
        Assert.False(fs.Files.ContainsKey("/data/PS5LM/models/b.gguf"));
    }

    [Fact]
    public async Task Delete_refuses_the_model_and_projectors_during_the_load_grace_window()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/a.gguf", new byte[1])
            .AddFile("/data/PS5LM/models/b.gguf", new byte[1])
            .AddFile("/data/PS5LM/models/mmproj-b.gguf", new byte[1]);
        _tracker.MarkRequested("/data/PS5LM/models/a.gguf");
        var control = Create(fs);

        var requested = await control.DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);
        var projector = await control.DeleteAsync("/data/PS5LM/models/mmproj-b.gguf", CancellationToken.None);
        var other = await control.DeleteAsync("/data/PS5LM/models/b.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, requested.Outcome);
        Assert.Equal(OperationOutcome.Conflict, projector.Outcome);
        Assert.Equal(OperationOutcome.Conflict, other.Outcome);
        Assert.Equal(CompanionLifecycle.Loading, _store.Current.Lifecycle);
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/mmproj-b.gguf"));
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/b.gguf"));
    }

    [Fact]
    public async Task Delete_refreshes_a_stale_idle_snapshot_when_the_console_is_loading()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);
        _store.Publish(CompanionStatus.Initial with { Lifecycle = CompanionLifecycle.AppAvailableNoModel });
        _llama.Result = new LlamaProbeResult(LlamaServerState.Loading, null, null);

        var result = await Create(fs).DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, result.Outcome);
        Assert.Equal(CompanionLifecycle.Loading, _store.Current.Lifecycle);
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
    }

    [Fact]
    public async Task Delete_matches_a_full_loaded_path_and_a_unique_bare_name_only()
    {
        var alone = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);
        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, null, "a.gguf");
        var unique = await Create(alone).DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);
        Assert.Equal(OperationOutcome.Conflict, unique.Outcome);
        Assert.True(alone.Files.ContainsKey("/data/PS5LM/models/a.gguf"));

        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/a.gguf", new byte[1])
            .AddFile("/mnt/usb0/PS5LM/models/a.gguf", new byte[1]);
        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, null, "/data/PS5LM/models/a.gguf");
        var control = Create(fs);

        var internalCopy = await control.DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);
        var usbCopy = await control.DeleteAsync("/mnt/usb0/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Conflict, internalCopy.Outcome);
        Assert.Equal(OperationOutcome.Completed, usbCopy.Outcome);
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
        Assert.False(fs.Files.ContainsKey("/mnt/usb0/PS5LM/models/a.gguf"));

        fs.AddFile("/mnt/usb0/PS5LM/models/a.gguf", new byte[1]);
        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, null, "a.gguf");
        var ambiguousUsb = await control.DeleteAsync("/mnt/usb0/PS5LM/models/a.gguf", CancellationToken.None);
        var nowUnique = await control.DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, ambiguousUsb.Outcome);
        Assert.Equal(OperationOutcome.Conflict, nowUnique.Outcome);
        Assert.False(fs.Files.ContainsKey("/mnt/usb0/PS5LM/models/a.gguf"));
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
    }

    [Fact]
    public async Task Delete_is_allowed_again_once_the_load_grace_window_has_passed()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);
        _tracker.MarkRequested("/data/PS5LM/models/a.gguf");
        _time.Advance(TimeSpan.FromSeconds(600));

        var result = await Create(fs).DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.False(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
    }

    [Fact]
    public async Task Delete_waits_for_a_pending_command_and_then_a_load_sees_the_file_gone()
    {
        var fs = ConsoleFixtures.PS5LMInstalled()
            .AddFile("/data/PS5LM/models/a.gguf", new byte[1])
            .AddFile("/data/PS5LM/unload", "-\n");
        var control = Create(fs);

        var blocked = await control.DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);
        Assert.Equal(OperationOutcome.Conflict, blocked.Outcome);
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));

        await fs.DeleteAsync("/data/PS5LM/unload", CancellationToken.None);
        Assert.Equal(OperationOutcome.Completed, (await control.DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None)).Outcome);
        Assert.Equal(OperationOutcome.NotFound, (await control.LoadAsync("/data/PS5LM/models/a.gguf", CancellationToken.None)).Outcome);
        Assert.Null(fs.Text("/data/PS5LM/load"));
    }

    [Fact]
    public async Task Sandboxed_deletion_uses_the_ftp_path()
    {
        var fs = ConsoleFixtures.PS5LMPlusInstalled(sandboxed: true)
            .AddFile("/mnt/sandbox/PPSA99581_000/download0/PS5LM/models/a.gguf", new byte[1]);

        var result = await Create(fs).DeleteAsync("/download0/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Completed, result.Outcome);
        Assert.False(fs.Files.ContainsKey("/mnt/sandbox/PPSA99581_000/download0/PS5LM/models/a.gguf"));
    }

    [Fact]
    public async Task Delete_does_not_report_success_when_the_file_is_still_listed()
    {
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);
        fs.IgnoreDeletes = true;

        var result = await Create(fs).DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.True(fs.Files.ContainsKey("/data/PS5LM/models/a.gguf"));
    }

    [Fact]
    public async Task Delete_is_unavailable_without_a_known_state_folder_and_throws_when_ftp_drops()
    {
        var missing = await Create(ConsoleFixtures.PS5LMInstalled(withState: false))
            .DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None);
        var fs = ConsoleFixtures.PS5LMInstalled().AddFile("/data/PS5LM/models/a.gguf", new byte[1]);
        var control = Create(fs);
        Assert.Equal(OperationOutcome.NotFound, (await control.DeleteAsync("/data/PS5LM/models/missing.gguf", CancellationToken.None)).Outcome);
        fs.Unreachable = true;

        Assert.Equal(OperationOutcome.Unavailable, missing.Outcome);
        await Assert.ThrowsAsync<PS5LM.Companion.Core.Abstractions.ConsoleUnreachableException>(() => control.DeleteAsync("/data/PS5LM/models/a.gguf", CancellationToken.None));
    }
}

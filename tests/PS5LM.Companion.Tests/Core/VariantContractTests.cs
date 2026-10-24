using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

/// <summary>
/// The adapter contract both apps must satisfy, walked end to end: a console with no model, a load request, the
/// app consuming the command file and restarting, llama-server coming up, then an unload. The fake console plays
/// the app's part by consuming the command files, as the real apps do about once a second.
/// </summary>
public sealed class VariantContractTests
{
    public sealed record Variant(string Name, PS5Variant Id, string StateFtp, string ModelAppPath, string Layout)
    {
        public override string ToString() => Name;

        internal FakeFileSystem Console() => Layout switch
        {
            "ps5lm" => ConsoleFixtures.PS5LMInstalled(),
            "plus" => ConsoleFixtures.PS5LMPlusInstalled(),
            _ => ConsoleFixtures.PS5LMPlusInstalled(sandboxed: true),
        };

        public string ModelFtpPath => $"{StateFtp}/models/{Path.GetFileName(ModelAppPath)}";
    }

    public static TheoryData<Variant> Variants() =>
    [
        new("PS5LM", PS5Variant.PS5LM, "/data/PS5LM", "/data/PS5LM/models/a.gguf", "ps5lm"),
        new("PS5LM Plus (data folder)", PS5Variant.PS5LMPlus, "/data/PS5LM", "/data/PS5LM/models/a.gguf", "plus"),
        new(
            "PS5LM Plus (sandbox)",
            PS5Variant.PS5LMPlus,
            "/mnt/sandbox/PPSA99581_000/download0/PS5LM",
            "/download0/PS5LM/models/a.gguf",
            "sandbox"),
    ];

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeLlamaProbe _llama = new();
    private readonly CompanionStatusStore _store = new();

    private (CompanionStatusService Status, ModelControlService Control) Create(FakeFileSystem fs)
    {
        var tracker = new LoadTracker(_time);
        var status = new CompanionStatusService(new PS5Options { Host = "ps5" }, fs, _llama, new FakeSensorProbe(), new FakePayloadCatalog(), new FakeElfldrProbe(), tracker, _time);
        return (status, new ModelControlService(fs, new ModelCatalog(fs), status, _store, tracker));
    }

    private async Task<CompanionStatus> RefreshAsync(CompanionStatusService status)
    {
        var current = await status.RefreshAsync(CancellationToken.None);
        _store.Publish(current);
        return current;
    }

    /// <summary>What the app does: take the command file away and restart, so port 8081 closes.</summary>
    private static string? AppConsumes(FakeFileSystem fs, Variant variant, string command)
    {
        var path = $"{variant.StateFtp}/{command}";
        var content = fs.Text(path);
        fs.DeleteAsync(path, CancellationToken.None).GetAwaiter().GetResult();
        return content;
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task The_status_before_any_model_comes_from_ftp_and_names_the_variant(Variant variant)
    {
        var (status, _) = Create(variant.Console());

        var current = await RefreshAsync(status);

        Assert.Equal(CompanionLifecycle.AppAvailableNoModel, current.Lifecycle);
        Assert.Equal(variant.Id, current.Variant);
        Assert.Equal(variant.StateFtp, current.ControlRoot);
        Assert.True(current.Capabilities.ModelControl);
        Assert.True(current.Capabilities.ModelUpload);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Load_and_unload_run_through_the_command_files_the_app_consumes(Variant variant)
    {
        var fs = variant.Console().AddFile(variant.ModelFtpPath, new byte[10]);
        var (status, control) = Create(fs);
        await RefreshAsync(status);

        // Load: the command file carries the path as the app sees it.
        var load = await control.LoadAsync(variant.ModelAppPath, CancellationToken.None);
        Assert.Equal(OperationOutcome.Accepted, load.Outcome);
        Assert.True((await RefreshAsync(status)).CommandPending);
        Assert.Equal(CompanionLifecycle.Loading, (await RefreshAsync(status)).Lifecycle);

        // The app consumes the file and restarts; 8081 stays closed for minutes. Still loading, not "no model".
        Assert.Equal(variant.ModelAppPath + "\n", AppConsumes(fs, variant, "load"));
        _time.Advance(TimeSpan.FromMinutes(2));
        var restarting = await RefreshAsync(status);
        Assert.Equal(CompanionLifecycle.Loading, restarting.Lifecycle);
        Assert.False(restarting.CommandPending);

        // llama-server opens its port while the model loads (503), then serves.
        _llama.Result = new LlamaProbeResult(LlamaServerState.Loading, null, null);
        Assert.Equal(CompanionLifecycle.Loading, (await RefreshAsync(status)).Lifecycle);
        _llama.Result = new LlamaProbeResult(LlamaServerState.Serving, FakeLlamaProbe.Metrics(5, 1), "a.gguf");
        var ready = await RefreshAsync(status);
        Assert.Equal(CompanionLifecycle.Ready, ready.Lifecycle);
        Assert.Equal("a.gguf", ready.LoadedModel);

        // The catalog flags the loaded model, and a repeat load is a no-op.
        var models = await new ModelCatalog(fs).ListAsync((await InstallationDetector.DetectAsync(fs, PS5Variant.Unknown, CancellationToken.None)), ready.LoadedModel, CancellationToken.None);
        Assert.True(models.Single().Loaded);
        Assert.Equal(OperationOutcome.NoChange, (await control.LoadAsync(variant.ModelAppPath, CancellationToken.None)).Outcome);

        // Unload: a dash, because the apps ignore an empty file. The app consumes it and restarts without a model.
        Assert.Equal(OperationOutcome.Accepted, (await control.UnloadAsync(CancellationToken.None)).Outcome);
        Assert.Equal("-\n", AppConsumes(fs, variant, "unload"));
        _llama.Result = LlamaProbeResult.Closed;
        Assert.Equal(CompanionLifecycle.AppAvailableNoModel, (await RefreshAsync(status)).Lifecycle);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task A_command_nobody_consumes_stays_visible_so_a_stopped_app_is_noticed(Variant variant)
    {
        var fs = variant.Console().AddFile(variant.ModelFtpPath, new byte[10]);
        var (status, control) = Create(fs);
        await RefreshAsync(status);
        await control.LoadAsync(variant.ModelAppPath, CancellationToken.None);

        _time.Advance(TimeSpan.FromMinutes(30));
        var current = await RefreshAsync(status);

        Assert.True(current.CommandPending);
        Assert.Equal(CompanionLifecycle.AppAvailableNoModel, current.Lifecycle);
        Assert.Contains("not picked it up", current.Message);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task A_console_that_goes_away_and_comes_back_recovers_without_a_restart(Variant variant)
    {
        var fs = variant.Console();
        var (status, _) = Create(fs);
        Assert.Equal(CompanionLifecycle.AppAvailableNoModel, (await RefreshAsync(status)).Lifecycle);

        fs.Unreachable = true;
        Assert.Equal(CompanionLifecycle.Disconnected, (await RefreshAsync(status)).Lifecycle);

        fs.Unreachable = false;
        var back = await RefreshAsync(status);
        Assert.Equal(CompanionLifecycle.AppAvailableNoModel, back.Lifecycle);
        Assert.Equal(variant.Id, back.Variant);
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public async Task Models_in_the_apps_extra_roots_and_on_usb_are_offered_with_the_apps_path(Variant variant)
    {
        var fs = variant.Console()
            .AddFile(variant.ModelFtpPath, new byte[1])
            .AddFile("/mnt/usb0/PS5LM/models/usb.gguf", new byte[1]);

        var installation = await InstallationDetector.DetectAsync(fs, PS5Variant.Unknown, CancellationToken.None);
        var models = await new ModelCatalog(fs).ListAsync(installation, null, CancellationToken.None);

        Assert.Contains(models, m => m.AppPath == variant.ModelAppPath);
        Assert.Contains(models, m => m.AppPath == "/mnt/usb0/PS5LM/models/usb.gguf");
    }

    [Fact]
    public async Task PS5LM_Plus_app_folder_model_uses_the_app_path_in_the_load_command()
    {
        var fs = ConsoleFixtures.PS5LMPlusInstalled(sandboxed: true)
            .AddFile("/data/homebrew/PPSA99581/models/bundled.gguf", new byte[1]);

        var (_, control) = Create(fs);
        var result = await control.LoadAsync("/app0/models/bundled.gguf", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("/app0/models/bundled.gguf\n", fs.Text("/mnt/sandbox/PPSA99581_000/download0/PS5LM/load"));
    }

    [Fact]
    public async Task PS5LM_Plus_prefers_the_state_folder_the_app_really_uses_when_both_exist()
    {
        var fs = ConsoleFixtures.PS5LMPlusInstalled(sandboxed: true).AddDirectory("/data/PS5LM");

        var installation = await InstallationDetector.DetectAsync(fs, PS5Variant.Unknown, CancellationToken.None);

        Assert.Equal("/mnt/sandbox/PPSA99581_000/download0/PS5LM", installation.StateRoot?.FtpPath);
    }
}

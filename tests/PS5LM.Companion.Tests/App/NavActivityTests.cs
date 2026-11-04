using Bunit;

using MudBlazor.Services;

using PS5LM.Companion.App.Components;
using PS5LM.Companion.App.Components.Layout;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Tests.App;

public sealed class NavActivityTests
{
    [Fact]
    public async Task A_busy_indicator_is_a_spinner_with_its_label()
    {
        var (markup, label) = await Render(busy: true, pending: false, "Loading model");

        Assert.Contains("mud-progress-circular", markup);
        Assert.DoesNotContain("nav-activity-dot", markup);
        Assert.Equal("Loading model", label);
    }

    [Fact]
    public async Task A_pending_indicator_is_a_static_dot()
    {
        var (markup, label) = await Render(busy: false, pending: true, "2 queued");

        Assert.Contains("nav-activity-dot", markup);
        Assert.DoesNotContain("mud-progress-circular", markup);
        Assert.Equal("2 queued", label);
    }

    [Fact]
    public async Task Work_in_progress_wins_over_pending()
    {
        var (markup, _) = await Render(busy: true, pending: true, "Transfer in progress, 2 queued");

        Assert.Contains("mud-progress-circular", markup);
        Assert.DoesNotContain("nav-activity-dot", markup);
    }

    [Fact]
    public async Task An_idle_indicator_renders_nothing()
    {
        var (markup, label) = await Render(busy: false, pending: false, "");

        Assert.DoesNotContain("nav-activity", markup);
        Assert.Null(label);
    }

    [Theory]
    [InlineData(CompanionLifecycle.Loading, false, "Loading model")]
    [InlineData(CompanionLifecycle.Ready, true, "Command pending (load or unload)")]
    [InlineData(CompanionLifecycle.Loading, true, "Loading model")]
    [InlineData(CompanionLifecycle.Ready, false, null)]
    [InlineData(CompanionLifecycle.Generating, false, null)]
    public void Model_activity_follows_the_lifecycle_and_pending_commands(
        CompanionLifecycle lifecycle, bool commandPending, string? expected)
    {
        var status = CompanionStatus.Initial with { Lifecycle = lifecycle, CommandPending = commandPending };

        Assert.Equal(expected, Display.ModelActivity(status));
    }

    [Fact]
    public void Transfer_activity_separates_a_running_transfer_from_queued_ones()
    {
        var transfers = new List<Transfer>
        {
            Sample(TransferState.Downloading),
            Sample(TransferState.Queued),
            Sample(TransferState.Queued),
            Sample(TransferState.Completed),
            Sample(TransferState.Failed),
        };

        Assert.Equal((true, 2), Display.TransferActivity(transfers));
    }

    [Fact]
    public void Transfer_activity_counts_verifying_and_uploading_as_running()
    {
        Assert.Equal((true, 0), Display.TransferActivity([Sample(TransferState.Verifying), Sample(TransferState.Uploading)]));
    }

    [Theory]
    [InlineData(true, 0, "Transfer in progress")]
    [InlineData(true, 2, "Transfer in progress, 2 queued")]
    [InlineData(false, 1, "1 queued")]
    [InlineData(false, 0, null)]
    public void Transfer_activity_label_names_a_running_transfer_without_a_phase(bool running, int queued, string? expected)
    {
        Assert.Equal(expected, Display.TransferActivityLabel(running, queued));
    }

    [Fact]
    public void Transfer_activity_is_idle_when_nothing_is_active()
    {
        Assert.Equal((false, 0), Display.TransferActivity([Sample(TransferState.Completed), Sample(TransferState.Cancelled)]));
        Assert.Equal((false, 1), Display.TransferActivity([Sample(TransferState.Queued)]));
    }

    private static Transfer Sample(TransferState state) => new(
        Guid.NewGuid(), "ps5", "owner/repo", "model.gguf", null, 100, "/data",
        state, 0, 0, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static async Task<(string Markup, string? Label)> Render(bool busy, bool pending, string label)
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var cut = ctx.Render<NavActivity>(parameters => parameters
            .Add(p => p.Busy, busy)
            .Add(p => p.Pending, pending)
            .Add(p => p.Label, label));
        var activity = cut.FindAll(".nav-activity").SingleOrDefault();
        return (cut.Markup, activity?.GetAttribute("aria-label"));
    }
}

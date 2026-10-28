using Microsoft.Extensions.Time.Testing;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Models;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class AppControlTests
{
    private const string Lm = "PPSA99575";
    private const string Plus = "PPSA99581";

    private static AppProviderSnapshot Snap(AppProviderKind kind, string[] installed, bool known, string? active) =>
        new(kind, true, "v", installed, known, active, null);

    [Fact]
    public void Serving_llama_overrides_home_screen_focus_for_the_sole_installed_variant()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Lm], true, "NPXS4003")], LlamaServerState.Serving);

        Assert.Equal(AppRunState.Running, status.Apps.Single(a => a.TitleId == Lm).State);
        Assert.Equal(AppRunState.NotRunning, status.Apps.Single(a => a.TitleId == Plus).State);
    }

    [Fact]
    public void Serving_llama_is_ambiguous_between_two_installed_variants_when_focus_names_neither()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Lm, Plus], true, "NPXS4003")], LlamaServerState.Loading);

        Assert.All(status.Apps, app => Assert.Equal(AppRunState.RunningVariantUnknown, app.State));
    }

    [Fact]
    public void A_variant_the_service_names_as_running_stays_when_llama_serves()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Lm, Plus], true, Plus)], LlamaServerState.Serving);

        Assert.Equal(AppRunState.Running, status.Apps.Single(a => a.TitleId == Plus).State);
        Assert.Equal(AppRunState.NotRunning, status.Apps.Single(a => a.TitleId == Lm).State);
    }

    [Fact]
    public void Serving_llama_without_any_service_reports_an_unknown_variant_and_keeps_the_explanation()
    {
        var unavailable = AppControlEvaluator.Evaluate([AppProviderSnapshot.Unavailable(AppProviderKind.VoidShell)]);
        var status = AppControlEvaluator.Evaluate([AppProviderSnapshot.Unavailable(AppProviderKind.VoidShell)], LlamaServerState.Serving);

        Assert.False(status.Available);
        Assert.NotNull(unavailable.Message);
        Assert.Equal(unavailable.Message, status.Message);
        Assert.All(status.Apps, app => Assert.Equal(AppRunState.RunningVariantUnknown, app.State));
    }

    [Fact]
    public void Nothing_answering_reports_unavailable_rows()
    {
        var status = AppControlEvaluator.Evaluate([AppProviderSnapshot.Unavailable(AppProviderKind.VoidShell), AppProviderSnapshot.Unavailable(AppProviderKind.Websrv)]);

        Assert.False(status.Available);
        Assert.All(status.Apps, app => Assert.False(app.CanLaunch));
    }

    [Fact]
    public void VoidShell_is_preferred_over_websrv()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.Websrv, [Lm], false, null), Snap(AppProviderKind.VoidShell, [Lm, Plus], true, Lm)]);

        Assert.Equal("VoidShell", status.Provider);
        Assert.Equal(AppRunState.Running, status.Apps.Single(a => a.TitleId == Lm).State);
        Assert.Equal(AppRunState.NotRunning, status.Apps.Single(a => a.TitleId == Plus).State);
        Assert.All(status.Apps, app => Assert.True(app.Installed));
    }

    [Fact]
    public void A_truncated_foreground_id_fitting_both_installed_variants_is_ambiguous()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Lm, Plus], true, "PPSA99")]);

        Assert.All(status.Apps, app => Assert.Equal(AppRunState.RunningVariantUnknown, app.State));
        Assert.Contains("PPSA99", status.Message);
    }

    [Fact]
    public void VoidShell_V1_1_eight_character_ids_still_identify_the_variant()
    {
        // The demo console reports "PPSA9957" for PS5LM (PPSA99575): the prefix is unique to one variant.
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Lm, Plus], true, "PPSA9957")]);

        Assert.Equal(AppRunState.Running, status.Apps.Single(a => a.TitleId == Lm).State);
        Assert.Equal(AppRunState.NotRunning, status.Apps.Single(a => a.TitleId == Plus).State);
    }

    [Fact]
    public void A_truncated_id_fitting_one_installed_variant_is_that_variant()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Plus], true, "PPSA9958")]);

        Assert.Equal(AppRunState.Running, status.Apps.Single(a => a.TitleId == Plus).State);
        Assert.Equal(AppRunState.NotRunning, status.Apps.Single(a => a.TitleId == Lm).State);
    }

    [Fact]
    public void Another_title_in_the_foreground_means_not_running()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Lm, Plus], true, "PPSA01325")]);

        Assert.All(status.Apps, app => Assert.Equal(AppRunState.NotRunning, app.State));
    }

    [Fact]
    public void Websrv_alone_cannot_tell_what_runs()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.Websrv, [Lm], false, null)]);

        Assert.Equal("Websrv", status.Provider);
        Assert.All(status.Apps, app => Assert.Equal(AppRunState.Unknown, app.State));
        Assert.True(status.Apps.Single(a => a.TitleId == Lm).CanLaunch);
        Assert.False(status.Apps.Single(a => a.TitleId == Plus).CanLaunch);
    }

    private static (AppLaunchService Service, FakeAppProvider VoidShell, FakeAppProvider Websrv) Harness()
    {
        var voidShell = new FakeAppProvider(AppProviderKind.VoidShell);
        var websrv = new FakeAppProvider(AppProviderKind.Websrv);
        IReadOnlyList<IAppControlProvider> providers = [websrv, voidShell];
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        var status = new CompanionStatusService(
            new PS5Options { Host = "ps5" }, ConsoleFixtures.PS5LMInstalled(), new FakeLlamaProbe(), new FakeSensorProbe(),
            new FakePayloadCatalog(), new FakeElfldrProbe(), new LoadTracker(time), time, providers);
        return (new AppLaunchService(providers, status, new CompanionStatusStore()), voidShell, websrv);
    }

    [Fact]
    public async Task Launch_uses_VoidShell_first()
    {
        var (service, voidShell, websrv) = Harness();
        voidShell.SetUp([Lm, Plus]);
        websrv.SetUp([Lm, Plus]);

        var result = await service.LaunchAsync(PS5Variant.PS5LMPlus, CancellationToken.None);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Equal([Plus], voidShell.Launched);
        Assert.Empty(websrv.Launched);
    }

    [Fact]
    public async Task Launch_falls_back_to_websrv_when_VoidShell_is_down()
    {
        var (service, _, websrv) = Harness();
        websrv.SetUp([Lm], runningKnown: false);

        var result = await service.LaunchAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("legacy fallback", result.Message, StringComparison.Ordinal);
        Assert.Equal([Lm], websrv.Launched);
    }

    [Fact]
    public async Task Launch_falls_back_when_VoidShell_cannot_be_reached_while_dispatching()
    {
        var (service, voidShell, websrv) = Harness();
        voidShell.SetUp([Lm]);
        voidShell.Result = new AppLaunchResult(false, false, "reset");
        websrv.SetUp([Lm], runningKnown: false);

        var result = await service.LaunchAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([Lm], websrv.Launched);
    }

    [Fact]
    public async Task A_rejected_launch_does_not_fall_back()
    {
        var (service, voidShell, websrv) = Harness();
        voidShell.SetUp([Lm]);
        voidShell.Result = new AppLaunchResult(true, false, "HTTP 500.");
        websrv.SetUp([Lm], runningKnown: false);

        var result = await service.LaunchAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Empty(websrv.Launched);
    }

    [Fact]
    public async Task An_uninstalled_title_is_not_launched()
    {
        var (service, voidShell, _) = Harness();
        voidShell.SetUp([Lm]);

        var result = await service.LaunchAsync(PS5Variant.PS5LMPlus, CancellationToken.None);

        Assert.Equal(OperationOutcome.NotFound, result.Outcome);
        Assert.Empty(voidShell.Launched);
    }

    [Fact]
    public async Task No_service_means_unavailable_and_unknown_variant_is_invalid()
    {
        var (service, _, _) = Harness();

        Assert.Equal(OperationOutcome.Unavailable, (await service.LaunchAsync(PS5Variant.PS5LM, CancellationToken.None)).Outcome);
        Assert.Equal(OperationOutcome.Invalid, (await service.LaunchAsync(PS5Variant.Unknown, CancellationToken.None)).Outcome);
    }
}

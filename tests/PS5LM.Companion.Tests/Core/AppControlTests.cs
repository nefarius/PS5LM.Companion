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
        var status = AppControlEvaluator.Evaluate([AppProviderSnapshot.Unavailable(AppProviderKind.VoidShell)]);

        Assert.False(status.Available);
        Assert.Contains("did not answer", status.Message, StringComparison.Ordinal);
        Assert.All(status.Apps, app => Assert.False(app.CanLaunch));
    }

    [Fact]
    public void A_VoidShell_that_does_not_know_the_running_title_still_accepts_a_later_report()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Lm], false, null), Snap(AppProviderKind.VoidShell, [Plus], true, Plus)]);

        Assert.Equal("VoidShell", status.Provider);
        Assert.True(status.Apps.Single(a => a.TitleId == Lm).Installed);
        Assert.False(status.Apps.Single(a => a.TitleId == Plus).Installed);
        Assert.Equal(AppRunState.NotRunning, status.Apps.Single(a => a.TitleId == Lm).State);
        Assert.Equal(AppRunState.Running, status.Apps.Single(a => a.TitleId == Plus).State);
        Assert.Null(status.Message);
    }

    [Fact]
    public void A_later_truncated_id_is_not_assigned_from_the_primary_library_alone()
    {
        var status = AppControlEvaluator.Evaluate([Snap(AppProviderKind.VoidShell, [Lm], false, null), Snap(AppProviderKind.VoidShell, [Plus], true, "PPSA99")]);

        Assert.Equal("VoidShell", status.Provider);
        Assert.True(status.Apps.Single(a => a.TitleId == Lm).Installed);
        Assert.False(status.Apps.Single(a => a.TitleId == Plus).Installed);
        Assert.False(status.Apps.Single(a => a.TitleId == Plus).CanLaunch);
        Assert.All(status.Apps, app => Assert.Equal(AppRunState.RunningVariantUnknown, app.State));
        Assert.Contains("PPSA99", status.Message);
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

    private static (AppLaunchService Service, FakeAppProvider VoidShell) Harness()
    {
        var voidShell = new FakeAppProvider(AppProviderKind.VoidShell);
        IReadOnlyList<IAppControlProvider> providers = [voidShell];
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero));
        var status = new CompanionStatusService(
            new PS5Options { Host = "ps5" }, ConsoleFixtures.PS5LMInstalled(), new FakeLlamaProbe(), new FakeSensorProbe(),
            new FakePayloadCatalog(), new FakeElfldrProbe(), new LoadTracker(time), time, providers);
        return (new AppLaunchService(providers, status, new CompanionStatusStore()), voidShell);
    }

    [Fact]
    public async Task Launch_uses_VoidShell()
    {
        var (service, voidShell) = Harness();
        voidShell.SetUp([Lm, Plus]);

        var result = await service.LaunchAsync(PS5Variant.PS5LMPlus, CancellationToken.None);

        Assert.Equal(OperationOutcome.Accepted, result.Outcome);
        Assert.Equal([Plus], voidShell.Launched);
    }

    [Fact]
    public async Task A_launch_that_never_reaches_VoidShell_is_unavailable()
    {
        var (service, voidShell) = Harness();
        voidShell.SetUp([Lm]);
        voidShell.Result = new AppLaunchResult(false, false, "reset");

        var result = await service.LaunchAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("not reachable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_launch_stays_unavailable()
    {
        var (service, voidShell) = Harness();
        voidShell.SetUp([Lm]);
        voidShell.Result = new AppLaunchResult(true, false, "HTTP 500.");

        var result = await service.LaunchAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, result.Outcome);
        Assert.Contains("HTTP 500", result.Message, StringComparison.Ordinal);
        Assert.Equal([Lm], voidShell.Launched);
    }

    [Fact]
    public async Task An_uninstalled_title_is_not_launched()
    {
        var (service, voidShell) = Harness();
        voidShell.SetUp([Lm]);

        var result = await service.LaunchAsync(PS5Variant.PS5LMPlus, CancellationToken.None);

        Assert.Equal(OperationOutcome.NotFound, result.Outcome);
        Assert.Empty(voidShell.Launched);
    }

    [Fact]
    public async Task No_service_means_unavailable_and_unknown_variant_is_invalid()
    {
        var (service, _) = Harness();

        var unavailable = await service.LaunchAsync(PS5Variant.PS5LM, CancellationToken.None);

        Assert.Equal(OperationOutcome.Unavailable, unavailable.Outcome);
        Assert.Contains("not reachable", unavailable.Message, StringComparison.Ordinal);
        Assert.Equal(OperationOutcome.Invalid, (await service.LaunchAsync(PS5Variant.Unknown, CancellationToken.None)).Outcome);
    }
}

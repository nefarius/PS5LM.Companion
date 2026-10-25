using PS5LM.Companion.Core.Abstractions;

namespace PS5LM.Companion.Tests.Support;

internal sealed class FakeAppProvider(AppProviderKind kind) : IAppControlProvider
{
    public AppProviderKind Kind { get; } = kind;

    public AppProviderSnapshot Snapshot { get; set; } = AppProviderSnapshot.Unavailable(kind, "down");

    public AppLaunchResult Result { get; set; } = new(true, true, "queued");

    public List<string> Launched { get; } = [];

    public Task<AppProviderSnapshot> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);

    public Task<AppLaunchResult> LaunchAsync(string titleId, CancellationToken cancellationToken)
    {
        Launched.Add(titleId);
        return Task.FromResult(Result);
    }

    public void SetUp(IEnumerable<string> installed, bool runningKnown = true, string? active = null) =>
        Snapshot = new AppProviderSnapshot(Kind, true, "test", installed.ToList(), runningKnown, active, null);
}

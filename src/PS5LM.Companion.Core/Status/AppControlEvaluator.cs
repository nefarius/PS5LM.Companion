using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Builds one per-variant view from the app-control services. The first service that answered (the VoidShell fork,
/// when it is up) supplies the installed titles and, when it knows, the running title. A later service supplies the
/// running title only when that primary service does not know it. A live llama-server proves that an app runs, so it
/// overrides the reported running state.
/// </summary>
public static class AppControlEvaluator
{
    /// <param name="snapshots">What each service reported.</param>
    /// <param name="llama">The llama-server state; serving or loading means some PS5LM app runs.</param>
    public static AppControlStatus Evaluate(
        IReadOnlyList<AppProviderSnapshot> snapshots,
        LlamaServerState llama = LlamaServerState.Closed)
    {
        var status = EvaluateServices(snapshots);
        return llama is LlamaServerState.Serving or LlamaServerState.Loading ? WithLlamaProof(status) : status;
    }

    /// <summary>
    /// llama-server proves that a PS5LM app runs, not which one. A variant the services already named as running
    /// stays; otherwise the sole installed variant is it; otherwise the owner is unknown. The configured variant
    /// is not evidence: it says which app the companion manages, not which one is serving.
    /// </summary>
    private static AppControlStatus WithLlamaProof(AppControlStatus status)
    {
        if (status.Apps.Count(a => a.State == AppRunState.Running) == 1)
        {
            return status;
        }

        var installed = status.Apps.Where(a => a.Installed).ToList();
        var owner = installed.Count == 1 ? installed[0].Variant : (PS5Variant?)null;
        var apps = status.Apps.Select(a => a with
        {
            State = owner is null
                ? AppRunState.RunningVariantUnknown
                : (a.Variant == owner ? AppRunState.Running : AppRunState.NotRunning),
        }).ToList();

        // Service notes ("cannot report which app runs", ambiguous title) are settled by llama-server;
        // an unavailable-service explanation stays.
        return status with { Apps = apps, Message = status.Available ? null : status.Message };
    }

    private static AppControlStatus EvaluateServices(IReadOnlyList<AppProviderSnapshot> snapshots)
    {
        var available = snapshots.Where(s => s.Available).OrderBy(s => s.Kind).ToList();
        if (available.Count == 0)
        {
            var websrvTried = snapshots.Any(snapshot => snapshot.Kind == AppProviderKind.Websrv);
            return new AppControlStatus(
                false,
                null,
                null,
                websrvTried
                    ? "The fixed VoidShell fork did not answer, and websrv (legacy fallback) did not answer either, so the companion cannot check or launch PS5LM."
                    : "The fixed VoidShell fork did not answer, so the companion cannot check or launch PS5LM.",
                VariantProfile.All.Select(p => new AppVariantStatus(p.Variant, p.TitleId, p.DisplayName, false, AppRunState.Unknown, false)).ToList());
        }

        var primary = available[0];
        var installed = primary.InstalledTitleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = primary.RunningKnown ? primary : available.FirstOrDefault(snapshot => snapshot.RunningKnown);

        var installedProfiles = VariantProfile.All.Where(p => installed.Contains(p.TitleId)).ToList();
        var runningScope = running is null || ReferenceEquals(running, primary) ? installedProfiles : VariantProfile.All;
        string? note = null;
        var apps = new List<AppVariantStatus>();
        foreach (var profile in VariantProfile.All)
        {
            var isInstalled = installed.Contains(profile.TitleId);
            var state = AppRunState.Unknown;
            if (running is not null)
            {
                state = RunState(profile, running.ActiveTitleId, runningScope, out var truncated);
                if (truncated)
                {
                    note = $"{running.Kind} reported the running title as '{running.ActiveTitleId}', which fits more than one PS5LM variant.";
                }
            }

            apps.Add(new AppVariantStatus(profile.Variant, profile.TitleId, profile.DisplayName, isInstalled, state, isInstalled));
        }

        note ??= running is null
            ? primary.Kind == AppProviderKind.Websrv
                ? "websrv is a legacy fallback and cannot report which app is running. The fixed VoidShell fork did not answer."
                : $"{primary.Kind} cannot report which app is running."
            : null;
        return new AppControlStatus(true, primary.Kind.ToString(), primary.Version, note, apps);
    }

    private static AppRunState RunState(VariantProfile profile, string? active, IReadOnlyList<VariantProfile> installed, out bool truncated)
    {
        truncated = false;
        if (string.IsNullOrWhiteSpace(active))
        {
            return AppRunState.NotRunning;
        }

        active = active.Trim();
        if (active.Equals(profile.TitleId, StringComparison.OrdinalIgnoreCase))
        {
            return AppRunState.Running;
        }

        if (VariantProfile.All.Any(p => p.TitleId.Equals(active, StringComparison.OrdinalIgnoreCase)))
        {
            return AppRunState.NotRunning;
        }

        // A truncated ID: only a prefix of a variant's title ID counts.
        var candidates = installed.Where(p => p.TitleId.StartsWith(active, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0)
        {
            candidates = VariantProfile.All.Where(p => p.TitleId.StartsWith(active, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (candidates.Count == 0 || active.Length < 6)
        {
            return AppRunState.NotRunning;
        }

        if (!candidates.Contains(profile))
        {
            return AppRunState.NotRunning;
        }

        if (candidates.Count == 1)
        {
            return AppRunState.Running;
        }

        truncated = true;
        return AppRunState.RunningVariantUnknown;
    }
}

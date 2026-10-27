using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Merges the app-control services' snapshots into one per-variant view. VoidShell wins over websrv. A live
/// llama-server is proof the app runs, whatever has focus, so it overrides the services' running state.
/// </summary>
public static class AppControlEvaluator
{
    /// <param name="snapshots">What each service reported.</param>
    /// <param name="llama">The llama-server state; serving or loading means the managed app runs.</param>
    /// <param name="managedVariant">The variant the companion manages, or null/Unknown when it cannot tell.</param>
    public static AppControlStatus Evaluate(
        IReadOnlyList<AppProviderSnapshot> snapshots,
        LlamaServerState llama = LlamaServerState.Closed,
        PS5Variant? managedVariant = null)
    {
        var status = EvaluateServices(snapshots);
        return llama is LlamaServerState.Serving or LlamaServerState.Loading ? WithLlamaProof(status, managedVariant) : status;
    }

    private static AppControlStatus WithLlamaProof(AppControlStatus status, PS5Variant? managedVariant)
    {
        var known = managedVariant is { } v && v != PS5Variant.Unknown && status.Apps.Any(a => a.Variant == v);
        var apps = status.Apps.Select(a => a with
        {
            State = known
                ? (a.Variant == managedVariant ? AppRunState.Running : AppRunState.NotRunning)
                : AppRunState.RunningVariantUnknown,
        }).ToList();

        // Service notes ("cannot report which app runs", ambiguous title) are settled by llama-server.
        var note = status.Available ? null : status.Message;
        return status with { Apps = apps, Message = note };
    }

    private static AppControlStatus EvaluateServices(IReadOnlyList<AppProviderSnapshot> snapshots)
    {
        var available = snapshots.Where(s => s.Available).OrderBy(s => s.Kind).ToList();
        if (available.Count == 0)
        {
            return new AppControlStatus(
                false,
                null,
                null,
                "Neither VoidShell nor websrv answered, so the companion cannot check or launch PS5LM.",
                VariantProfile.All.Select(p => new AppVariantStatus(p.Variant, p.TitleId, p.DisplayName, false, AppRunState.Unknown, false)).ToList());
        }

        var primary = available[0];
        var installed = available.SelectMany(s => s.InstalledTitleIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = available.FirstOrDefault(s => s.RunningKnown);

        var installedProfiles = VariantProfile.All.Where(p => installed.Contains(p.TitleId)).ToList();
        string? note = null;
        var apps = new List<AppVariantStatus>();
        foreach (var profile in VariantProfile.All)
        {
            var isInstalled = installed.Contains(profile.TitleId);
            var state = AppRunState.Unknown;
            if (running is not null)
            {
                state = RunState(profile, running.ActiveTitleId, installedProfiles, out var truncated);
                if (truncated)
                {
                    note = $"{running.Kind} reported the foreground title as '{running.ActiveTitleId}', which fits more than one PS5LM variant.";
                }
            }

            apps.Add(new AppVariantStatus(profile.Variant, profile.TitleId, profile.DisplayName, isInstalled, state, isInstalled));
        }

        note ??= running is null ? $"{primary.Kind} cannot report which app is running." : null;
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

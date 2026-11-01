using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.Core.Updates;

/// <summary>The last hash comparison of stored files with the pinned manifest.</summary>
public sealed record ComponentUpdateSnapshot(
    IReadOnlyList<ComponentUpdate> Updates,
    DateTimeOffset? CheckedAt,
    bool Checking,
    string? Failure)
{
    public static ComponentUpdateSnapshot Empty { get; } = new([], null, false, null);
}

/// <summary>Combines cheap live version checks with the slower hash comparison.</summary>
public static class ComponentUpdates
{
    /// <summary>
    /// Flags Payload Manager and VoidShell only when the running version is strictly older than the pin.
    /// A missing or unparseable version is left to the hash check.
    /// </summary>
    public static IReadOnlyList<ComponentUpdate> Live(SetupManifest? manifest, string? managerVersion, string? voidShellSemVer)
    {
        if (manifest is null)
        {
            return [];
        }

        var result = new List<ComponentUpdate>();
        if (PayloadManagerVersion.TryParse(managerVersion, out var running)
            && PayloadManagerVersion.TryParse(manifest.PayloadManager.Version, out var target)
            && running < target)
        {
            result.Add(new ComponentUpdate(
                PayloadDependencies.ManagerId,
                "Payload Manager",
                manifest.PayloadManager.Version,
                $"{managerVersion} -> {manifest.PayloadManager.Version}"));
        }

        var voidShell = manifest.Payloads.FirstOrDefault(payload =>
            payload.Id.Equals(PayloadDependencies.VoidShell, StringComparison.OrdinalIgnoreCase));
        if (voidShell is not null
            && SemanticVersion.TryParse(voidShellSemVer, out var runningFork)
            && SemanticVersion.TryParse(voidShell.Version, out var pinnedFork)
            && runningFork.CompareTo(pinnedFork) < 0)
        {
            result.Add(new ComponentUpdate(
                voidShell.Id,
                voidShell.Name,
                voidShell.Version,
                $"{voidShellSemVer} -> {voidShell.Version}"));
        }

        return result;
    }

    /// <summary>
    /// A component is outdated when either source says so. Duplicate ids keep the first name and pin and join details.
    /// When a live Payload Manager version was read, that result replaces any hash-check row for it.
    /// </summary>
    public static ComponentUpdateStatus Merge(
        IReadOnlyList<ComponentUpdate> live,
        ComponentUpdateSnapshot snapshot,
        bool liveManagerVersion = false)
    {
        var hash = liveManagerVersion
            ? snapshot.Updates.Where(item => !item.Id.Equals(PayloadDependencies.ManagerId, StringComparison.OrdinalIgnoreCase))
            : snapshot.Updates;
        var byId = new Dictionary<string, ComponentUpdate>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in live.Concat(hash))
        {
            if (byId.TryGetValue(item.Id, out var existing))
            {
                if (!existing.Detail.Contains(item.Detail, StringComparison.Ordinal))
                {
                    byId[item.Id] = existing with { Detail = existing.Detail + "; " + item.Detail };
                }

                continue;
            }

            byId[item.Id] = item;
        }

        return new ComponentUpdateStatus([.. byId.Values], snapshot.CheckedAt, snapshot.Checking, snapshot.Failure);
    }

    /// <summary>The manifest app id for a PS5LM variant, or null when the variant is unknown.</summary>
    public static string? AppId(PS5Variant variant) => variant switch
    {
        PS5Variant.PS5LM => "ps5lm",
        PS5Variant.PS5LMPlus => "ps5lmplus",
        _ => null,
    };
}

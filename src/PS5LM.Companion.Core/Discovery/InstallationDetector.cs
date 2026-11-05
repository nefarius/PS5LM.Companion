using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Updates;

namespace PS5LM.Companion.Core.Discovery;

/// <summary>The outcome of looking at the console over FTP.</summary>
/// <param name="Profile">The detected app, or <see langword="null"/> when none (or ambiguous).</param>
/// <param name="StateRoot">The folder holding the app's state; <see langword="null"/> until the app has run once.</param>
/// <param name="Message">A human-readable reason when something is missing.</param>
public sealed record Installation(VariantProfile? Profile, StorageRoot? StateRoot, string? Message)
{
    /// <summary>
    /// The installed app's version from <c>sce_sys/param.json</c>, or null when that file is missing or not a content version.
    /// A null version does not hide a state root.
    /// </summary>
    public SemanticVersion? AppVersion { get; init; }
}

/// <summary>
/// Works out which app is installed and where its state lives, using FTP only, since llama-server
/// does not listen before a model is loaded.
/// </summary>
public static class InstallationDetector
{
    /// <exception cref="ConsoleUnreachableException">The console's FTP service is down.</exception>
    public static async Task<Installation> DetectAsync(
        IPS5FileSystem fs,
        PS5Variant configured,
        CancellationToken cancellationToken)
    {
        var profile = configured != PS5Variant.Unknown ? VariantProfile.For(configured) : null;
        if (profile is null)
        {
            var installed = new List<VariantProfile>();
            foreach (var candidate in VariantProfile.All)
            {
                if (await fs.DirectoryExistsAsync(candidate.InstallPath, cancellationToken))
                {
                    installed.Add(candidate);
                }
            }

            switch (installed.Count)
            {
                case 0:
                    return new Installation(null, null, "Neither PS5LM nor PS5LM Plus is installed under /data/homebrew.");
                case > 1:
                    return new Installation(
                        null,
                        null,
                        "PS5LM and PS5LM Plus are both installed; set PS5:Variant to choose which one to manage.");
                default:
                    profile = installed[0];
                    break;
            }
        }

        var version = await ReadAppVersionAsync(fs, profile, cancellationToken);
        var root = await FindStateRootAsync(fs, profile, version, cancellationToken);
        return root is null
            ? new Installation(profile, null, $"{profile.DisplayName} has not created its state folder yet; start the app once.") { AppVersion = version }
            : new Installation(profile, root, null) { AppVersion = version };
    }

    private static async Task<SemanticVersion?> ReadAppVersionAsync(
        IPS5FileSystem fs,
        VariantProfile profile,
        CancellationToken cancellationToken)
    {
        var param = await fs.ReadTextAsync($"{profile.InstallPath}/sce_sys/param.json", 64 * 1024, cancellationToken);
        return AppVersion.TryParseParam(param, out var version) ? version : null;
    }

    private static async Task<StorageRoot?> FindStateRootAsync(
        IPS5FileSystem fs,
        VariantProfile profile,
        SemanticVersion? installed,
        CancellationToken cancellationToken)
    {
        // A root introduced by a later app version stays hidden on an older console. A missing or
        // unreadable version checks every root, with /data/PS5LM still first.
        var roots = profile.StateRoots.Where(root => Supports(root, installed)).ToList();

        // The root with a log or settings file is the one the app really uses. Both apps may be in
        // their own storage when the HEN keeps /data closed.
        var withState = new List<StorageRoot>();
        foreach (var root in roots)
        {
            if (await HasStateAsync(fs, root, cancellationToken))
            {
                withState.Add(root);
            }
        }

        switch (withState.Count)
        {
            case 0:
                foreach (var root in roots)
                {
                    if (await fs.DirectoryExistsAsync(root.FtpPath, cancellationToken))
                    {
                        return root;
                    }
                }

                return null;
            case 1:
                return withState[0];
            default:
                // Stale files can sit in /data/PS5LM after the app moved. The newer app.log is the live one;
                // missing or equal times keep the earlier root, which is /data/PS5LM.
                return await FreshestAsync(fs, withState, cancellationToken);
        }
    }

    private static bool Supports(StorageRoot root, SemanticVersion? installed) =>
        installed is null || root.Since is not SemanticVersion since || installed.Value.CompareTo(since) >= 0;

    private static async Task<bool> HasStateAsync(IPS5FileSystem fs, StorageRoot root, CancellationToken cancellationToken) =>
        await fs.FileExistsAsync($"{root.FtpPath}/settings.json", cancellationToken)
        || await fs.FileExistsAsync($"{root.FtpPath}/app.log", cancellationToken);

    private static async Task<StorageRoot> FreshestAsync(
        IPS5FileSystem fs,
        IReadOnlyList<StorageRoot> candidates,
        CancellationToken cancellationToken)
    {
        var best = candidates[0];
        var bestTime = await LogModifiedAsync(fs, best, cancellationToken);
        for (var index = 1; index < candidates.Count; index++)
        {
            var time = await LogModifiedAsync(fs, candidates[index], cancellationToken);
            if (time is not null && (bestTime is null || time > bestTime))
            {
                best = candidates[index];
                bestTime = time;
            }
        }

        return best;
    }

    private static async Task<DateTimeOffset?> LogModifiedAsync(
        IPS5FileSystem fs,
        StorageRoot root,
        CancellationToken cancellationToken)
    {
        var entries = await fs.TryListAsync(root.FtpPath, cancellationToken);
        return entries?.FirstOrDefault(entry => entry.Name == "app.log")?.Modified;
    }
}

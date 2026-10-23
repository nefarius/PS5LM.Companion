using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Discovery;

/// <summary>The outcome of looking at the console over FTP.</summary>
/// <param name="Profile">The detected app, or <see langword="null"/> when none (or ambiguous).</param>
/// <param name="StateRoot">The folder holding the app's state; <see langword="null"/> until the app has run once.</param>
/// <param name="Message">A human-readable reason when something is missing.</param>
public sealed record Installation(VariantProfile? Profile, StorageRoot? StateRoot, string? Message);

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

        var root = await FindStateRootAsync(fs, profile, cancellationToken);
        return root is null
            ? new Installation(profile, null, $"{profile.DisplayName} has not created its state folder yet; start the app once.")
            : new Installation(profile, root, null);
    }

    private static async Task<StorageRoot?> FindStateRootAsync(
        IPS5FileSystem fs,
        VariantProfile profile,
        CancellationToken cancellationToken)
    {
        // The root with a log or settings file is the one the app really uses (PS5LM Plus may be in its sandbox).
        foreach (var root in profile.StateRoots)
        {
            if (await fs.FileExistsAsync($"{root.FtpPath}/settings.json", cancellationToken)
                || await fs.FileExistsAsync($"{root.FtpPath}/app.log", cancellationToken))
            {
                return root;
            }
        }

        foreach (var root in profile.StateRoots)
        {
            if (await fs.DirectoryExistsAsync(root.FtpPath, cancellationToken))
            {
                return root;
            }
        }

        return null;
    }
}

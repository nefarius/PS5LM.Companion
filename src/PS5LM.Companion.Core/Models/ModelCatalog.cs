using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Models;

/// <summary>Lists the GGUF files on the console and the places a new one may be written to.</summary>
public sealed class ModelCatalog(IPS5FileSystem fileSystem)
{
    /// <summary>Every model folder that exists on the console, in the app's search order.</summary>
    public async Task<IReadOnlyList<StorageRoot>> ModelRootsAsync(Installation installation, CancellationToken cancellationToken)
    {
        if (installation.Profile is null || installation.StateRoot is null)
        {
            return [];
        }

        IEnumerable<StorageRoot> candidates =
        [
            VariantProfile.ModelsOf(installation.StateRoot),
            .. installation.Profile.ExtraModelRoots,
        ];

        var roots = new List<StorageRoot>();
        foreach (var candidate in candidates)
        {
            if (await fileSystem.DirectoryExistsAsync(candidate.FtpPath, cancellationToken))
            {
                roots.Add(candidate);
            }
        }

        return roots;
    }

    /// <summary>Where uploads may go: writable folders that exist, or can be created below a mounted drive.</summary>
    public async Task<IReadOnlyList<StorageRoot>> UploadTargetsAsync(Installation installation, CancellationToken cancellationToken)
    {
        if (installation.Profile is null || installation.StateRoot is not { Writable: true } state)
        {
            return [];
        }

        var targets = new List<StorageRoot> { VariantProfile.ModelsOf(state) };
        foreach (var drive in installation.Profile.ExtraModelRoots.Where(r => r.Writable))
        {
            // The drive itself (/mnt/usb0) must be mounted; PS5LM/models is created on the first upload.
            var mount = MountOf(drive.FtpPath);
            if (await fileSystem.DirectoryExistsAsync(mount, cancellationToken))
            {
                targets.Add(drive);
            }
        }

        return targets;
    }

    public async Task<IReadOnlyList<ModelFile>> ListAsync(
        Installation installation,
        string? loadedModelId,
        CancellationToken cancellationToken)
    {
        var models = new List<ModelFile>();
        foreach (var root in await ModelRootsAsync(installation, cancellationToken))
        {
            foreach (var entry in await fileSystem.ListAsync(root.FtpPath, cancellationToken))
            {
                if (entry.IsDirectory || !entry.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var appPath = $"{root.AppPath.TrimEnd('/')}/{entry.Name}";
                models.Add(new ModelFile(
                    entry.Name,
                    entry.FullPath,
                    appPath,
                    entry.Size,
                    root.Name,
                    ModelNames.IsSameModel(loadedModelId, entry.Name, appPath),
                    root.Writable));
            }
        }

        return models.OrderBy(m => m.IsProjector).ThenBy(m => m.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"/mnt/usb0/PS5LM/models" is on the drive mounted at "/mnt/usb0".</summary>
    private static string MountOf(string ftpPath)
    {
        var parts = ftpPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "mnt" ? $"/mnt/{parts[1]}" : ftpPath;
    }
}

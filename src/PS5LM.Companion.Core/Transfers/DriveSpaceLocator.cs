using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Transfers;

/// <summary>
/// Maps a console folder to the filesystem ps5-exporter reports it on. A title cannot read free space itself
/// (statvfs is a stub), so without the exporter the answer is "unknown".
/// </summary>
public static class DriveSpaceLocator
{
    public static long? FreeBytes(ConsoleSensors? sensors, StorageRoot root)
    {
        if (sensors is null || sensors.Drives.Count == 0)
        {
            return null;
        }

        var path = root.FtpPath;
        DriveSpace? drive = null;

        if (path.StartsWith("/mnt/sandbox/", StringComparison.Ordinal))
        {
            // The app's own space: "/mnt/sandbox/PPSA99581_000/download0/..." is listed as ".../download/PPSA99581".
            var title = path["/mnt/sandbox/".Length..].Split('/')[0].Split('_')[0];
            drive = sensors.Drives.FirstOrDefault(d => d.MountPoint.Contains(title, StringComparison.Ordinal));
        }
        else if (path.StartsWith("/mnt/", StringComparison.Ordinal))
        {
            var mount = "/mnt/" + path["/mnt/".Length..].Split('/')[0];
            drive = sensors.Drives.FirstOrDefault(d => d.MountPoint == mount);
        }
        else if (path.StartsWith("/data/", StringComparison.Ordinal))
        {
            // /data is /user/data.
            drive = sensors.Drives.FirstOrDefault(d => d.MountPoint == "/user");
        }

        return drive is { TotalBytes: > 0 } ? drive.FreeBytes : null;
    }
}

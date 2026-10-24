using MudBlazor;

using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Components;

internal static class Display
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string Lifecycle(CompanionLifecycle lifecycle) => lifecycle switch
    {
        CompanionLifecycle.Disconnected => "Console unreachable",
        CompanionLifecycle.AppAvailableNoModel => "No model loaded",
        CompanionLifecycle.Loading => "Loading model",
        CompanionLifecycle.Ready => "Ready",
        CompanionLifecycle.Generating => "Generating",
        _ => lifecycle.ToString(),
    };

    public static Color LifecycleColor(CompanionLifecycle lifecycle) => lifecycle switch
    {
        CompanionLifecycle.Disconnected => Color.Error,
        CompanionLifecycle.AppAvailableNoModel => Color.Warning,
        CompanionLifecycle.Loading => Color.Info,
        CompanionLifecycle.Ready => Color.Success,
        CompanionLifecycle.Generating => Color.Primary,
        _ => Color.Default,
    };

    public static string Requirement(DependencyRequirement requirement) => requirement switch
    {
        DependencyRequirement.Required => "Required",
        DependencyRequirement.Recommended => "Recommended",
        DependencyRequirement.Bundled => "Bundled",
        _ => requirement.ToString(),
    };

    public static string Presence(DependencyPresence presence) => presence switch
    {
        DependencyPresence.Unknown => "Unknown",
        DependencyPresence.NotInstalled => "Not installed",
        DependencyPresence.Installed => "Installed",
        DependencyPresence.Bundled => "Bundled",
        _ => presence.ToString(),
    };

    public static string Health(DependencyHealth health) => health switch
    {
        DependencyHealth.Unavailable => "Unavailable",
        DependencyHealth.Unknown => "Unknown",
        DependencyHealth.LaunchRecorded => "Launch recorded",
        DependencyHealth.Verified => "Verified",
        DependencyHealth.NotRunning => "Not running",
        _ => health.ToString(),
    };

    public static Color HealthColor(DependencyHealth health) => health switch
    {
        DependencyHealth.Verified => Color.Success,
        DependencyHealth.LaunchRecorded => Color.Info,
        DependencyHealth.Unknown or DependencyHealth.NotRunning => Color.Warning,
        _ => Color.Default,
    };

    public static Color TransferColor(TransferState state) => state switch
    {
        TransferState.Completed => Color.Success,
        TransferState.Failed => Color.Error,
        TransferState.Cancelled => Color.Default,
        _ => Color.Info,
    };

    /// <summary>Overall progress: the download counts for the first half and the upload for the second.</summary>
    public static double Progress(Transfer transfer)
    {
        if (transfer.State == TransferState.Completed)
        {
            return 100;
        }

        if (transfer.SizeBytes <= 0)
        {
            return 0;
        }

        var done = (transfer.DownloadedBytes + transfer.UploadedBytes) / (2.0 * transfer.SizeBytes);
        return Math.Clamp(done * 100, 0, 100);
    }
}

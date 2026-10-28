namespace PS5LM.Companion.App.Updates;

/// <summary>The running version compared with the latest stable GitHub Release.</summary>
internal sealed record VersionCheckStatus(
    string? LocalVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    Uri? ReleaseUri)
{
    public bool HasLocalVersion => !string.IsNullOrWhiteSpace(LocalVersion);

    public string ChipLabel =>
        UpdateAvailable && !string.IsNullOrWhiteSpace(LatestVersion)
            ? $"{Display(LocalVersion)} · {Display(LatestVersion)} available"
            : Display(LocalVersion);

    public string AriaLabel => $"Version {ChipLabel}";

    public string Tooltip =>
        !HasLocalVersion
            ? "Version unavailable"
            : UpdateAvailable && !string.IsNullOrWhiteSpace(LatestVersion)
                ? $"Running version {Display(LocalVersion)}. Version {Display(LatestVersion)} is available."
                : $"Running version {Display(LocalVersion)}.";

    private static string Display(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return "unknown";
        }

        var trimmed = version.Trim();
        return trimmed[0] is 'v' or 'V' ? trimmed : "v" + trimmed;
    }
}

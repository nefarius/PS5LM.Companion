namespace PS5LM.Companion.Core.Setup;

/// <summary>
/// The lines in ShadowMountPlus's <c>debug.log</c> that mean a title was registered.
/// A skipped scan mentions the title too, and that is not an install.
/// </summary>
internal static class ShadowMountLog
{
    public static int CountInstalled(string? log, string titleId) =>
        log is null
            ? 0
            : log.Split('\n').Count(line => line.Contains("installed game", StringComparison.OrdinalIgnoreCase)
                && line.Contains(titleId, StringComparison.OrdinalIgnoreCase));
}

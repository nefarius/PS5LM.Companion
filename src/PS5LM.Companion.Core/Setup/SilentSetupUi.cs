namespace PS5LM.Companion.Core.Setup;

/// <summary>A setup UI that never writes and never consents. Used by the in-process update check.</summary>
public sealed class SilentSetupUi : ISetupUi
{
    public static SilentSetupUi Instance { get; } = new();

    public bool CanPrompt => false;

    public void Info(string message)
    {
    }

    public bool Confirm(string question) => false;
}

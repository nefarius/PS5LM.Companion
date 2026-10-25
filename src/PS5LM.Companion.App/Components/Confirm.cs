using MudBlazor;

using PS5LM.Companion.Core.Consoles;

namespace PS5LM.Companion.App.Components;

/// <summary>Yes/cancel prompts for actions that change a console.</summary>
internal static class Confirm
{
    public static string On(ConsoleRuntime console) => $"{console.Name} ({console.Host})";

    public static async Task<bool> AskAsync(IDialogService dialogs, string title, string message, string yesText)
    {
        var confirmed = await dialogs.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = title,
            Message = message,
            YesText = yesText,
            CancelText = "Cancel",
        });
        return confirmed == true;
    }
}

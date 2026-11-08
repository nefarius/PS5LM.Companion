using Microsoft.AspNetCore.Components;

using MudBlazor;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Components;

/// <summary>Test, edit and delete for one console, shared by the drawer and the consoles page.</summary>
internal static class ConsoleActions
{
    public static Task TestAsync(IDialogService dialogs, ConsoleRuntime console) =>
        ConsoleProbeDialog.ShowAsync(dialogs, console);

    public static async Task EditAsync(IDialogService dialogs, NavigationManager navigation, ConsoleRuntime console)
    {
        var saved = await ConsoleEditor.ShowAsync(dialogs, console.Id);
        if (saved is null || string.Equals(saved, console.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (ReplaceConsoleId(navigation, console.Id, saved) is { } target)
        {
            navigation.NavigateTo(target);
        }
    }

    public static async Task RemoveAsync(
        IDialogService dialogs,
        ISnackbar snackbar,
        IConsoleRegistry registry,
        TransferService transfers,
        ConsoleRuntime console,
        string confirmText = "Remove")
    {
        if (transfers.HasActive(console.Id))
        {
            var active = transfers.Snapshot(console.Id).Where(transfer => transfer.IsActive).ToList();
            var listed = active.Count == 0
                ? "a copy from this console is still running"
                : string.Join("; ", active.Select(transfer => $"{transfer.FileName} ({transfer.State})"));
            await dialogs.ShowMessageBoxAsync(new MessageBoxOptions
            {
                Title = $"Cannot remove {console.Name}",
                Message = $"Cancel these transfers first: {listed}",
                CancelText = "Close",
            });
            return;
        }

        if (!await Confirm.AskAsync(
                dialogs,
                $"{confirmText} {console.Name}",
                $"Remove {Confirm.On(console)} from this companion? The console itself is left as it is.",
                confirmText))
        {
            return;
        }

        var result = registry.Remove(console.Id);
        snackbar.Add(
            result.Succeeded ? $"{console.Name} was removed." : result.Message ?? "Could not remove the console.",
            result.Succeeded ? Severity.Success : Severity.Error);
    }

    /// <summary>The current address with one console id replaced, including a query or fragment.</summary>
    private static string? ReplaceConsoleId(NavigationManager navigation, string previousId, string newId)
    {
        var relative = navigation.ToBaseRelativePath(navigation.Uri);
        var suffix = string.Empty;
        var cut = relative.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            suffix = relative[cut..];
            relative = relative[..cut];
        }

        const string prefix = "consoles/";
        if (!relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = relative[prefix.Length..];
        var slash = rest.IndexOf('/');
        var id = slash < 0 ? rest : rest[..slash];
        if (!string.Equals(id, previousId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var tail = slash < 0 ? string.Empty : rest[slash..];
        return $"/{prefix}{newId}{tail}{suffix}";
    }
}

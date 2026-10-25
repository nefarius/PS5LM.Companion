using Microsoft.AspNetCore.Components;

using MudBlazor;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Components;

/// <summary>
/// A page for one console, taken from the <c>ConsoleId</c> route value. It re-renders when that console's status
/// or a transfer changes. Updates are coalesced because transfer progress is reported many times a second.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(250);

    private int _scheduled;
    private bool _disposed;
    private string? _boundId;
    private CompanionStatusStore? _subscribed;

    [Inject]
    protected IConsoleRegistry Consoles { get; set; } = default!;

    [Inject]
    protected TransferService Transfers { get; set; } = default!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = default!;

    [Inject]
    private NavigationManager Navigation { get; set; } = default!;

    /// <summary>The console id from the route, unchanged in case.</summary>
    [Parameter]
    public string? ConsoleId { get; set; }

    /// <summary>The console this page shows, or <see langword="null"/> when the route names none.</summary>
    protected ConsoleRuntime? Target => Consoles.Find(ConsoleId);

    protected CompanionStatus Status => Target?.Store.Current ?? CompanionStatus.Initial;

    protected override void OnInitialized()
    {
        Transfers.Changed += OnChanged;
        Consoles.Changed += OnRegistryChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (string.Equals(_boundId, ConsoleId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _boundId = ConsoleId;
        SubscribeToTarget();
        await OnConsoleChangedAsync();
    }

    /// <summary>Called when the route's console changes, including the first time, so the page can reload.</summary>
    protected virtual Task OnConsoleChangedAsync() => Task.CompletedTask;

    /// <summary>Called on the renderer's thread before a live re-render, e.g. to reload data a change made stale.</summary>
    protected virtual Task OnLiveChangeAsync() => Task.CompletedTask;

    private void OnRegistryChanged(ConsoleChange change)
    {
        if (change.Kind == ConsoleChangeKind.Removed
            && change.Previous is { } removed
            && string.Equals(removed.Id, ConsoleId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = InvokeAsync(() =>
                {
                    Snackbar.Add($"{removed.Name} was removed.", Severity.Info);
                    Navigation.NavigateTo("/");
                });
            }
            catch (ObjectDisposedException)
            {
                // The circuit ended.
            }

            return;
        }

        if (change.Kind == ConsoleChangeKind.Replaced
            && change.Current is { } current
            && string.Equals(current.Id, ConsoleId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = InvokeAsync(async () =>
                {
                    SubscribeToTarget();
                    await OnConsoleChangedAsync();
                    OnChanged();
                });
            }
            catch (ObjectDisposedException)
            {
                // The circuit ended.
            }
        }
    }

    private void SubscribeToTarget()
    {
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnStatusChanged;
        }

        _subscribed = Target?.Store;
        if (_subscribed is not null)
        {
            _subscribed.Changed += OnStatusChanged;
        }
    }

    private void OnStatusChanged(CompanionStatus _) => OnChanged();

    private void OnChanged()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
        {
            return;
        }

        _ = RefreshLaterAsync();
    }

    private async Task RefreshLaterAsync()
    {
        await Task.Delay(Coalesce);
        Interlocked.Exchange(ref _scheduled, 0);
        if (_disposed)
        {
            return;
        }

        try
        {
            await InvokeAsync(async () =>
            {
                await OnLiveChangeAsync();
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException)
        {
            // The circuit ended while the refresh was pending.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnStatusChanged;
        }

        Transfers.Changed -= OnChanged;
        Consoles.Changed -= OnRegistryChanged;
        GC.SuppressFinalize(this);
    }
}

using Microsoft.AspNetCore.Components;

using PS5LM.Companion.App.Consoles;
using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Status;
using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.App.Components;

/// <summary>
/// A component that re-renders when the selected console's status, the selection, or a transfer changes.
/// Updates are coalesced because transfer progress is reported many times a second.
/// </summary>
public abstract class LiveComponent : ComponentBase, IDisposable
{
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(250);

    private int _scheduled;
    private bool _disposed;

    private CompanionStatusStore? _subscribed;

    [Inject]
    protected ActiveConsole Active { get; set; } = default!;

    [Inject]
    protected TransferService Transfers { get; set; } = default!;

    /// <summary>The console this page currently shows.</summary>
    protected ConsoleRuntime Target => Active.Runtime;

    protected CompanionStatus Status => Target.Store.Current;

    protected override void OnInitialized()
    {
        SubscribeToTarget();
        Active.Changed += OnSelectionChanged;
        Transfers.Changed += OnChanged;
    }

    private void SubscribeToTarget()
    {
        if (_subscribed is not null)
        {
            _subscribed.Changed -= OnStatusChanged;
        }

        _subscribed = Target.Store;
        _subscribed.Changed += OnStatusChanged;
    }

    private void OnSelectionChanged()
    {
        SubscribeToTarget();
        OnChanged();
    }

    /// <summary>Called on the renderer's thread before a live re-render, e.g. to reload data a change made stale.</summary>
    protected virtual Task OnLiveChangeAsync() => Task.CompletedTask;

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

        Active.Changed -= OnSelectionChanged;
        Transfers.Changed -= OnChanged;
        GC.SuppressFinalize(this);
    }
}

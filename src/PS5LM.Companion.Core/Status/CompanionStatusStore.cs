using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Holds the latest <see cref="CompanionStatus"/> and tells subscribers (Blazor circuits, the REST layer)
/// when it changes. This is the small live-update bus: pages read <see cref="Current"/> and re-render on <see cref="Changed"/>.
/// </summary>
public sealed class CompanionStatusStore
{
    private volatile CompanionStatus _current = CompanionStatus.Initial;

    public CompanionStatus Current => _current;

    public event Action<CompanionStatus>? Changed;

    public void Publish(CompanionStatus status)
    {
        _current = status;
        Changed?.Invoke(status);
    }
}

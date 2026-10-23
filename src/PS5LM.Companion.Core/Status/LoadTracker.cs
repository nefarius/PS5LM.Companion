namespace PS5LM.Companion.Core.Status;

/// <summary>
/// Remembers that the companion asked the app to load a model. The app restarts itself and only opens its
/// inference port minutes later, so until then the lifecycle reads "loading" rather than "no model".
/// </summary>
public sealed class LoadTracker(TimeProvider time)
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _requestedAt;
    private string? _model;

    public string? RequestedModel
    {
        get
        {
            lock (_gate)
            {
                return _model;
            }
        }
    }

    public void MarkRequested(string model)
    {
        lock (_gate)
        {
            _requestedAt = time.GetUtcNow();
            _model = model;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _requestedAt = null;
            _model = null;
        }
    }

    /// <summary>Whether a load was requested less than <paramref name="grace"/> ago.</summary>
    public bool IsLoading(TimeSpan grace)
    {
        lock (_gate)
        {
            return _requestedAt is { } at && time.GetUtcNow() - at < grace;
        }
    }
}

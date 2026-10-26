namespace PS5LM.Companion.Core.Inference;

/// <summary>
/// The opt-in inference gateway. Bound from <c>Inference</c>; the gateway is on when at least one pool is configured:
/// <c>Inference:Pools:{alias}:Model</c> and <c>Inference:Pools:{alias}:Consoles:0</c>.
/// </summary>
public sealed class InferenceOptions
{
    public const string SectionName = "Inference";

    /// <summary>Public model aliases keyed by the name clients send as <c>model</c>.</summary>
    public Dictionary<string, InferencePoolOptions> Pools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Requests one console serves at once. PS5LM starts llama-server with one slot, so the default is 1.</summary>
    public int MaxConcurrentPerConsole { get; set; } = 1;

    /// <summary>Requests that may wait for a free replica, across all pools. Further requests get <c>429</c>.</summary>
    public int MaxQueuedRequests { get; set; } = 16;

    /// <summary>How long a request waits for a free replica before it gets <c>503</c>.</summary>
    public int QueueTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How long an upstream request may stay silent (no bytes either way) before it is cancelled.
    /// Prompt evaluation on a console can take minutes before the first token.
    /// </summary>
    public int UpstreamTimeoutSeconds { get; set; } = 900;

    /// <summary>Largest request body the gateway reads to find the model, in mebibytes.</summary>
    public int MaxRequestBodyMegabytes { get; set; } = 32;

    public bool Enabled => Pools.Count > 0;
}

/// <summary>The consoles that serve one public model alias.</summary>
public sealed class InferencePoolOptions
{
    /// <summary>
    /// The model the consoles must report as loaded: a bare file name (matches any folder) or the full path
    /// llama-server reports (matches exactly).
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Console ids that may serve this alias.</summary>
    public List<string> Consoles { get; set; } = [];

    /// <summary>Other <c>model</c> names clients may send: exact names, or prefixes ending in <c>*</c> such as <c>claude-*</c>.</summary>
    public List<string> AlternateNames { get; set; } = [];
}

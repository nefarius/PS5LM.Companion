namespace PS5LM.Companion.Core.Models.Planning;

/// <summary>
/// The GGUF metadata the planner needs. Field meanings match PS5LM's <c>ModelInfo</c>
/// (<c>ps5/app/model_plan.hpp</c>).
/// </summary>
public sealed record GgufModelInfo
{
    public string FileName { get; init; } = string.Empty;

    /// <summary><c>general.name</c>, or the file name when that key is missing.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary><c>general.architecture</c>.</summary>
    public string Architecture { get; init; } = string.Empty;

    /// <summary><c>&lt;arch&gt;.decision.type</c>. Empty for a chat model.</summary>
    public string Decision { get; init; } = string.Empty;

    public ulong FileBytes { get; init; }

    /// <summary>Block count without the MTP (next-n) blocks.</summary>
    public uint LayerCount { get; init; }

    /// <summary>Feed-forward width of the widest layer.</summary>
    public uint FeedForwardLength { get; init; }

    public uint KeyValueHeads { get; init; }

    public uint KeyLength { get; init; }

    public uint ValueLength { get; init; }

    /// <summary>1 when every layer is attention. Hybrid models keep a KV cache every Nth layer.</summary>
    public uint FullAttentionInterval { get; init; } = 1;

    /// <summary>Attention layers that keep only a sliding window.</summary>
    public uint SlidingWindowLayers { get; init; }

    /// <summary>That window, in tokens.</summary>
    public uint SlidingWindow { get; init; }

    public uint TrainedContext { get; init; }

    /// <summary>0 for a dense model.</summary>
    public uint ExpertCount { get; init; }

    /// <summary>Recurrent state of every layer, in bytes (f32).</summary>
    public ulong StateBytes { get; init; }

    public bool Ok { get; init; }

    public string? Error { get; init; }

    /// <summary>Layers that keep a KV cache. Sliding-window layers are included; the planner accounts for them apart.</summary>
    public uint KeyValueLayers => FullAttentionInterval > 1 ? LayerCount / FullAttentionInterval : LayerCount;
}

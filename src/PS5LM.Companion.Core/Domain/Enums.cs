namespace PS5LM.Companion.Core.Domain;

/// <summary>The PS5LM app flavour installed on the console.</summary>
public enum PS5Variant
{
    /// <summary>Not detected, or both flavours are installed and none was configured.</summary>
    Unknown = 0,

    /// <summary><c>cobanov/PS5LM</c>, title ID <c>PPSA99575</c>.</summary>
    PS5LM = 1,

    /// <summary><c>abhi245y/PS5LM-Plus</c>, title ID <c>PPSA99581</c>.</summary>
    PS5LMPlus = 2,
}

/// <summary>
/// Where the console is in its lifecycle. The apps only listen on the llama.cpp port once a model is loaded,
/// so everything before <see cref="Loading"/> is derived from FTP alone.
/// </summary>
public enum CompanionLifecycle
{
    /// <summary>The console's FTP service cannot be reached.</summary>
    Disconnected = 0,

    /// <summary>FTP works and an app is installed, but llama-server is not listening (no model loaded).</summary>
    AppAvailableNoModel = 1,

    /// <summary>llama-server answers but is still loading the model, or a load was requested and is in progress.</summary>
    Loading = 2,

    /// <summary>A model is loaded and idle.</summary>
    Ready = 3,

    /// <summary>A model is loaded and processing a request.</summary>
    Generating = 4,
}

/// <summary>What the llama-server probe on the inference port observed.</summary>
public enum LlamaServerState
{
    /// <summary>Nothing listens (connection refused or timed out): no model is loaded.</summary>
    Closed = 0,

    /// <summary>The server answers with 503 "Loading model".</summary>
    Loading = 1,

    /// <summary>The server serves metrics.</summary>
    Serving = 2,
}

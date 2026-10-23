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

/// <summary>How important a console payload is for a first run of PS5LM Plus.</summary>
public enum DependencyRequirement
{
    /// <summary>The app does not run without it.</summary>
    Required = 0,

    /// <summary>The app runs, and installation, telemetry or launching other payloads needs it.</summary>
    Recommended = 1,

    /// <summary>Shipped inside PS5LM Plus and started by the app.</summary>
    Bundled = 2,
}

/// <summary>Whether Payload Manager has the payload, as far as the companion could see.</summary>
public enum DependencyPresence
{
    /// <summary>Payload Manager has not been checked, or it did not answer.</summary>
    Unknown = 0,

    /// <summary>The payload is absent from Payload Manager's installed list.</summary>
    NotInstalled = 1,

    /// <summary>An installed slot with this id was found.</summary>
    Installed = 2,

    /// <summary>The payload is part of PS5LM Plus, not a separate Payload Manager entry.</summary>
    Bundled = 3,
}

/// <summary>
/// Whether a payload is active. A recorded launch is evidence that Payload Manager sent the file,
/// which is weaker than a service that answers on its own port.
/// </summary>
public enum DependencyHealth
{
    /// <summary>The companion has not checked yet, or Payload Manager is required for this answer and did not respond.</summary>
    Unavailable = 0,

    /// <summary>No positive evidence. A failed port check also lands here, with the reason in the message.</summary>
    Unknown = 1,

    /// <summary>Payload Manager recorded a successful transfer. It cannot tell whether the payload is still active.</summary>
    LaunchRecorded = 2,

    /// <summary>An external check saw the service answering.</summary>
    Verified = 3,
}

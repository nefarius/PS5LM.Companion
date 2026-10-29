using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Abstractions;

/// <summary>What llama-server reported on one probe.</summary>
/// <param name="State">Closed when nothing listens, which is the normal state before a model is loaded.</param>
/// <param name="MetricsText">Prometheus text from <c>/metrics</c> when <see cref="LlamaServerState.Serving"/>.</param>
/// <param name="LoadedModel">The model id from <c>/v1/models</c>, when known.</param>
/// <param name="MetricsObservedAt">
/// When the <c>/metrics</c> request was sent: the reported counters are from that moment or later.
/// Unset when the probe does not know.
/// </param>
public sealed record LlamaProbeResult(LlamaServerState State, string? MetricsText, string? LoadedModel, DateTimeOffset? MetricsObservedAt = null)
{
    public static LlamaProbeResult Closed { get; } = new(LlamaServerState.Closed, null, null);
}

/// <summary>Probes the llama-server the app starts on port 8081 once a model is loaded.</summary>
public interface ILlamaServerProbe
{
    /// <summary>Never throws for an unreachable server: that is reported as <see cref="LlamaServerState.Closed"/>.</summary>
    Task<LlamaProbeResult> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Reads ps5-exporter's sensors and drive space (port 9100) when the payload is loaded.</summary>
public interface IConsoleSensorProbe
{
    /// <summary>Returns <see langword="null"/> when ps5-exporter is not running.</summary>
    Task<ConsoleSensors?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads Payload Manager's installed payloads and the launches it has recorded.</summary>
public interface IPayloadCatalog
{
    /// <summary>Never throws because the manager is down: that is <see cref="PayloadCatalogSnapshot.Available"/> false.</summary>
    Task<PayloadCatalogSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Sends an installed payload slot to Payload Manager, which forwards the file to elfldr.</summary>
public interface IPayloadLauncher
{
    /// <summary>
    /// <see cref="PayloadLaunchResult.Accepted"/> means the file was transferred.
    /// It does not mean an unprobeable payload finished starting.
    /// </summary>
    Task<PayloadLaunchResult> LaunchAsync(string slot, CancellationToken cancellationToken);
}

/// <summary>Checks whether elfldr accepts TCP connections. Payload Manager cannot launch anything without it.</summary>
public interface IElfldrProbe
{
    /// <summary>False when the port is closed, filtered or the check times out. Caller cancellation still propagates.</summary>
    Task<bool> IsListeningAsync(CancellationToken cancellationToken);
}

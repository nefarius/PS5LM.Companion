using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Domain;
using PS5LM.Companion.Core.Telemetry;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>Reads ps5-exporter; a missing exporter is normal, not an error.</summary>
public sealed class ExporterSensorProbe(IPS5ExporterApi api) : IConsoleSensorProbe
{
    public async Task<ConsoleSensors?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await api.GetMetricsAsync(cancellationToken);
            return response.IsSuccessStatusCode && !string.IsNullOrEmpty(response.Content)
                ? ExporterReader.Read(response.Content)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

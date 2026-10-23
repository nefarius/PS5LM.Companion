using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Telemetry;

/// <summary>Reads ps5-exporter's Prometheus text into <see cref="ConsoleSensors"/>.</summary>
public static class ExporterReader
{
    // sceKernelGetSocPowerConsumption reports milliwatts in the low 32 bits; the high half is a sample counter.
    private const ulong LowWordMask = 0xffffffffUL;
    private const ulong MinMilliwatts = 1_000;
    private const ulong MaxMilliwatts = 350_000;

    public static ConsoleSensors Read(string metricsText)
    {
        var metrics = PrometheusText.Parse(metricsText);

        double? power = null;
        if (metrics.Get("ps5_soc_power_raw_value") is { } raw && raw >= 0)
        {
            var milliwatts = (ulong)raw & LowWordMask;
            if (milliwatts is >= MinMilliwatts and <= MaxMilliwatts)
            {
                power = milliwatts / 1000.0;
            }
        }

        var drives = new List<DriveSpace>();
        foreach (var size in metrics.All("ps5_filesystem_size_bytes"))
        {
            if (!size.Labels.TryGetValue("mountpoint", out var mount))
            {
                continue;
            }

            var free = metrics.All("ps5_filesystem_avail_bytes")
                .FirstOrDefault(s => s.Labels.TryGetValue("mountpoint", out var m) && m == mount);
            drives.Add(new DriveSpace(mount, (long)size.Value, free is null ? 0 : (long)free.Value));
        }

        return new ConsoleSensors(
            Temperature(metrics, "soc0"),
            Temperature(metrics, "cpu"),
            power,
            metrics.Get("ps5_fan_duty_ratio") is { } fan && fan >= 0 ? fan : null,
            drives);
    }

    private static double? Temperature(PrometheusText metrics, string sensor) =>
        metrics.Get("ps5_temperature_celsius", "sensor", sensor) is { } t && t is > -100 and < 200 ? t : null;
}

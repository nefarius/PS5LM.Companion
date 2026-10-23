using PS5LM.Companion.Core.Telemetry;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Core;

public sealed class TelemetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_sample_has_counters_but_no_speed()
    {
        var snapshot = new TelemetryCalculator().Update(FakeLlamaProbe.Metrics(100, 5), Now);

        Assert.NotNull(snapshot);
        Assert.Equal(100, snapshot.TokensGenerated);
        Assert.Equal(0, snapshot.GenerationTokensPerSecond);
    }

    [Fact]
    public void Speed_is_tokens_per_second_of_compute_time_between_samples()
    {
        var calculator = new TelemetryCalculator();
        calculator.Update(FakeLlamaProbe.Metrics(100, 5), Now);

        var snapshot = calculator.Update(FakeLlamaProbe.Metrics(160, 7, processing: 1), Now.AddSeconds(3));

        Assert.NotNull(snapshot);
        Assert.Equal(30, snapshot.GenerationTokensPerSecond, 3);
        Assert.Equal(1, snapshot.RequestsProcessing);
    }

    [Fact]
    public void Idle_server_reports_zero_speed()
    {
        var calculator = new TelemetryCalculator();
        calculator.Update(FakeLlamaProbe.Metrics(100, 5), Now);

        var snapshot = calculator.Update(FakeLlamaProbe.Metrics(100, 5), Now.AddSeconds(3));

        Assert.Equal(0, snapshot!.GenerationTokensPerSecond);
    }

    [Fact]
    public void Text_without_llama_counters_yields_no_snapshot() =>
        Assert.Null(new TelemetryCalculator().Update("unrelated 1", Now));

    [Fact]
    public void Exporter_power_uses_the_low_word_in_milliwatts_and_ignores_garbage()
    {
        var good = ExporterReader.Read("ps5_soc_power_raw_value 86000\nps5_temperature_celsius{sensor=\"soc0\"} 70\nps5_fan_duty_ratio 0.4");
        var counterOnly = ExporterReader.Read("ps5_soc_power_raw_value 4294967296");
        var unread = ExporterReader.Read("ps5_temperature_celsius{sensor=\"soc0\"} -1000");

        Assert.Equal(86.0, good.SocPowerWatts);
        Assert.Equal(70, good.SocTemperatureC);
        Assert.Equal(0.4, good.FanDuty);
        Assert.Null(counterOnly.SocPowerWatts);
        Assert.Null(unread.SocTemperatureC);
    }

    [Fact]
    public void Exporter_drives_pair_size_with_free_space_by_mount_point()
    {
        var sensors = ExporterReader.Read(
            """
            ps5_filesystem_size_bytes{mountpoint="/user",fstype="ufs"} 1000
            ps5_filesystem_avail_bytes{mountpoint="/user",fstype="ufs"} 400
            ps5_filesystem_size_bytes{mountpoint="/mnt/usb0",fstype="exfat"} 5000
            ps5_filesystem_avail_bytes{mountpoint="/mnt/usb0",fstype="exfat"} 2500
            """);

        Assert.Equal(2, sensors.Drives.Count);
        Assert.Equal(2500, sensors.Drives.Single(d => d.MountPoint == "/mnt/usb0").FreeBytes);
    }
}

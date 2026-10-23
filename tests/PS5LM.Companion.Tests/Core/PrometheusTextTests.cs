using PS5LM.Companion.Core.Telemetry;

namespace PS5LM.Companion.Tests.Core;

public sealed class PrometheusTextTests
{
    [Fact]
    public void Ignores_comments_and_blank_lines_and_prefixes()
    {
        var text = PrometheusText.Parse("# HELP x\n\nllamacpp:tokens_predicted_total 42\nother 1.5\n");

        Assert.Equal(42, text.Get("tokens_predicted_total"));
        Assert.Equal(1.5, text.Get("other"));
        Assert.Null(text.Get("missing"));
    }

    [Fact]
    public void Reads_labels_and_filters_by_them()
    {
        var text = PrometheusText.Parse(
            """
            ps5_temperature_celsius{sensor="soc0"} 61
            ps5_temperature_celsius{sensor="cpu"} 55
            """);

        Assert.Equal(55, text.Get("ps5_temperature_celsius", "sensor", "cpu"));
        Assert.Equal(61, text.Get("ps5_temperature_celsius", "sensor", "soc0"));
        Assert.Null(text.Get("ps5_temperature_celsius", "sensor", "gpu"));
    }

    [Fact]
    public void Handles_braces_and_escapes_inside_label_values_and_timestamps()
    {
        var text = PrometheusText.Parse("m{path=\"/a}b\",q=\"x\\\"y\"} 7 1700000000");

        var sample = Assert.Single(text.Samples);
        Assert.Equal("/a}b", sample.Labels["path"]);
        Assert.Equal("x\"y", sample.Labels["q"]);
        Assert.Equal(7, sample.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("name notanumber")]
    public void Skips_unparseable_lines(string line) => Assert.Empty(PrometheusText.Parse(line).Samples);
}

using System.Globalization;

namespace PS5LM.Companion.Core.Telemetry;

/// <summary>One sample line of Prometheus exposition text.</summary>
public sealed record PrometheusSample(string Name, IReadOnlyDictionary<string, string> Labels, double Value);

/// <summary>A small parser for the Prometheus text format, enough for llama-server and ps5-exporter.</summary>
public sealed class PrometheusText
{
    private static readonly IReadOnlyDictionary<string, string> NoLabels = new Dictionary<string, string>();

    private PrometheusText(IReadOnlyList<PrometheusSample> samples) => Samples = samples;

    public IReadOnlyList<PrometheusSample> Samples { get; }

    public static PrometheusText Parse(string? text)
    {
        var samples = new List<PrometheusSample>();
        foreach (var raw in (text ?? string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (TryParseLine(line, out var sample))
            {
                samples.Add(sample);
            }
        }

        return new PrometheusText(samples);
    }

    /// <summary>
    /// The first sample called <paramref name="name"/> (a <c>prefix:</c> such as llama-server's <c>llamacpp:</c>
    /// is ignored), optionally filtered by a label.
    /// </summary>
    public double? Get(string name, string? labelName = null, string? labelValue = null)
    {
        foreach (var sample in Samples)
        {
            if (!NameMatches(sample.Name, name))
            {
                continue;
            }

            if (labelName is not null
                && (!sample.Labels.TryGetValue(labelName, out var value) || value != labelValue))
            {
                continue;
            }

            return sample.Value;
        }

        return null;
    }

    public IEnumerable<PrometheusSample> All(string name) => Samples.Where(s => NameMatches(s.Name, name));

    private static bool NameMatches(string actual, string wanted) =>
        actual.Equals(wanted, StringComparison.Ordinal)
        || actual.EndsWith(":" + wanted, StringComparison.Ordinal);

    private static bool TryParseLine(string line, out PrometheusSample sample)
    {
        sample = null!;
        string name;
        IReadOnlyDictionary<string, string> labels = NoLabels;
        string rest;

        var brace = line.IndexOf('{', StringComparison.Ordinal);
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (brace >= 0 && (space < 0 || brace < space))
        {
            name = line[..brace];
            var close = FindClosingBrace(line, brace);
            if (close < 0)
            {
                return false;
            }

            labels = ParseLabels(line[(brace + 1)..close]);
            rest = line[(close + 1)..].Trim();
        }
        else
        {
            if (space < 0)
            {
                return false;
            }

            name = line[..space];
            rest = line[(space + 1)..].Trim();
        }

        // A sample may carry a trailing timestamp: "<value> <timestamp>".
        var valueText = rest.Split(' ', 2)[0];
        if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        sample = new PrometheusSample(name, labels, value);
        return true;
    }

    private static int FindClosingBrace(string line, int open)
    {
        var inQuotes = false;
        for (var i = open + 1; i < line.Length; i++)
        {
            switch (line[i])
            {
                case '\\':
                    i++;
                    break;
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case '}' when !inQuotes:
                    return i;
            }
        }

        return -1;
    }

    private static Dictionary<string, string> ParseLabels(string text)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < text.Length)
        {
            var eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            var key = text[i..eq].Trim(' ', ',');
            var start = text.IndexOf('"', eq);
            if (start < 0)
            {
                break;
            }

            var end = start + 1;
            var value = new System.Text.StringBuilder();
            while (end < text.Length && text[end] != '"')
            {
                if (text[end] == '\\' && end + 1 < text.Length)
                {
                    end++;
                }

                value.Append(text[end]);
                end++;
            }

            labels[key] = value.ToString();
            i = end + 1;
        }

        return labels;
    }
}

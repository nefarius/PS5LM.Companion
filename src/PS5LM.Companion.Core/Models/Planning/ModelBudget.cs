using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PS5LM.Companion.Core.Models.Planning;

/// <summary>The free device memory and the planner limits the app would use for a model.</summary>
/// <param name="Gib">Free GPU memory, in GiB.</param>
/// <param name="CtxCap">The planner's longest context, from <c>settings.json</c> or the app's default.</param>
/// <param name="KvType"><c>auto</c>, <c>f16</c>, <c>q8_0</c> or <c>q4_0</c>.</param>
/// <param name="FromAppLog">False when <see cref="Gib"/> is the configured assumption rather than a line from <c>app.log</c>.</param>
public sealed partial record ModelBudget(double Gib, uint CtxCap, string KvType, bool FromAppLog)
{
    /// <summary>The free memory PS5LM was calibrated against, used when <c>app.log</c> has no device line.</summary>
    public const double CalibratedGib = 11.38;

    public const uint DefaultCtxCap = 65536;

    public const string AutoKv = "auto";

    /// <summary>
    /// The largest <c>app.log</c> that is read. The device line is written at each launch; a longer log is treated
    /// as missing and the calibrated budget is used.
    /// </summary>
    public const int MaxLogBytes = 8 * 1024 * 1024;

    private static readonly string[] KvTypes = [AutoKv, "f16", "q8_0", "q4_0"];

    /// <summary>The cache type passed to the planner. Null asks it to choose.</summary>
    public string? ForcedKvType => KvType == AutoKv ? null : KvType;

    public string Caption => FromAppLog
        ? $"{OneDecimal(Gib)} GiB free on this console, from app.log"
        : $"assumed {OneDecimal(Gib)} GiB";

    public static ModelBudget Assumed(double gib) => new(Positive(gib), DefaultCtxCap, AutoKv, false);

    /// <summary>
    /// The last non-CPU <c>device N name (description): free / total GiB free</c> line wins, which is how the app
    /// picks its GPU. <paramref name="settingsJson"/> supplies <c>ctx_cap</c> and <c>kv_type</c>; a missing or
    /// unreadable file keeps the app's defaults.
    /// </summary>
    public static ModelBudget Parse(string? appLog, string? settingsJson, double fallbackGib)
    {
        var (ctxCap, kvType) = ReadSettings(settingsJson);
        var free = DeviceGib(appLog);
        return new ModelBudget(free ?? Positive(fallbackGib), ctxCap, kvType, free is not null);
    }

    public static string OneDecimal(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static double Positive(double gib) => gib > 0 && !double.IsNaN(gib) && !double.IsInfinity(gib) ? gib : CalibratedGib;

    private static double? DeviceGib(string? appLog)
    {
        if (string.IsNullOrEmpty(appLog))
        {
            return null;
        }

        double? free = null;
        foreach (Match match in DeviceLine().Matches(appLog))
        {
            if (match.Groups["name"].Value.Equals("CPU", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (double.TryParse(match.Groups["free"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
            {
                free = value;
            }
        }

        return free;
    }

    private static (uint CtxCap, string KvType) ReadSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return (DefaultCtxCap, AutoKv);
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(settingsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            return (DefaultCtxCap, AutoKv);
        }

        if (node is not JsonObject obj)
        {
            return (DefaultCtxCap, AutoKv);
        }

        return (ReadCtxCap(obj), ReadKvType(obj));
    }

    private static uint ReadCtxCap(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("ctx_cap", out var node) || node is not JsonValue value)
        {
            return DefaultCtxCap;
        }

        if (value.TryGetValue<long>(out var whole) && whole is > 0 and <= uint.MaxValue)
        {
            return (uint)whole;
        }

        if (value.TryGetValue<double>(out var number) && number is > 0 and <= uint.MaxValue && number == Math.Truncate(number))
        {
            return (uint)number;
        }

        return DefaultCtxCap;
    }

    private static string ReadKvType(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("kv_type", out var node) || node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            return AutoKv;
        }

        foreach (var known in KvTypes)
        {
            if (string.Equals(text, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return AutoKv;
    }

    [GeneratedRegex(@"device\s+\d+\s+(?<name>\S+)\s+\(.*\):\s+(?<free>[0-9]+(?:\.[0-9]+)?)\s*/\s*[0-9]+(?:\.[0-9]+)?\s+GiB free", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();
}

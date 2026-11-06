using System.Globalization;

namespace PS5LM.Companion.Core.Models.Planning;

/// <summary>
/// Whether a model fits, and the context and KV-cache type it would be given.
/// A port of <c>plan_model</c> in PS5LM's <c>ps5/app/model_plan.cpp</c> at
/// <c>707768548dae7e4c9de29fb94a72ae03c383a46f</c>.
/// </summary>
/// <param name="Ctx">The context, in tokens. 0 when <see cref="Fits"/> is false.</param>
/// <param name="KvType"><c>f16</c>, <c>q8_0</c> or <c>q4_0</c>. Empty when <see cref="Fits"/> is false.</param>
/// <param name="Why">One line, in the same shape the app shows.</param>
public sealed record ModelPlan(
    bool Fits,
    uint Ctx,
    string KvType,
    double TotalGib,
    double BudgetGib,
    string Why,
    uint Ubatch,
    double ReserveGib);

/// <summary>
/// Picks the longest context and the best KV-cache type that fit the free device memory.
/// The reserve and the KV formulas are the app's, including counting a hybrid model's recurrent
/// state twice (the live state and one prompt checkpoint).
/// </summary>
public static class ModelPlanner
{
    public const double GiB = 1073741824.0;

    /// <summary>The fixed reserve the app's model page uses before it has read a header.</summary>
    public const double WeightReserveGib = 1.0;

    /// <summary>llama-server's <c>-ub</c> for a language model. Decision models and image encoders use 2048.</summary>
    public const uint PromptUbatch = 64;

    private static readonly uint[] Contexts = [262144, 196608, 131072, 65536, 32768, 16384, 8192, 4096];

    private static readonly string[] KvTypes = ["f16", "q8_0", "q4_0"];

    /// <summary>Substring of the file name, model name or architecture, and that preset's context cap. 0 means no cap.</summary>
    private static readonly (string Match, uint MaxContext)[] Presets =
    [
        ("bonsai", 65536),
        ("qwen3.8", 0),
        ("qwen3.6", 0),
        ("qwen3.5", 0),
        ("ornith", 0),
        ("gemma", 0),
        ("gpt-oss", 16384),
        ("granite", 0),
        ("nemotron", 0),
        ("mistral", 0),
        ("llama", 0),
    ];

    /// <summary>True when the weights plus the page's 1 GiB reserve are already over the budget, so the header is not worth reading.</summary>
    public static bool WeightsAloneExceed(long bytes, double budgetGib) => (bytes / GiB) + WeightReserveGib > budgetGib;

    public static ModelPlan Plan(GgufModelInfo model, double budgetGib, uint ctxCap = 65536, string? kvType = null, uint ubatch = PromptUbatch, ulong projectorBytes = 0)
    {
        var cap = PresetCap(model, ctxCap);
        if (!string.IsNullOrEmpty(model.Decision))
        {
            cap = Math.Min(cap, 4096);
        }

        if (model.TrainedContext > 0)
        {
            cap = Math.Min(cap, model.TrainedContext);
        }

        var wide = !string.IsNullOrEmpty(model.Decision) || projectorBytes > 0;
        var usedUbatch = wide ? 2048 : Math.Max(ubatch, 1);
        var stateGib = model.StateBytes / GiB;
        var ubExtra = !wide && usedUbatch > PromptUbatch
            ? (usedUbatch - PromptUbatch) * (double)model.FeedForwardLength * 38.0 / GiB
            : 0;
        var reserve = 0.75 + stateGib + (projectorBytes > 0 ? 0.25 : 0) + (wide ? 0.5 : 0) + ubExtra;
        var weights = (model.FileBytes + projectorBytes) / GiB;
        var forced = string.IsNullOrEmpty(kvType) || kvType == "auto" ? null : kvType;

        foreach (var ctx in Contexts)
        {
            if (ctx > cap)
            {
                continue;
            }

            foreach (var type in KvTypes)
            {
                if (forced is not null && !string.Equals(type, forced, StringComparison.Ordinal))
                {
                    continue;
                }

                // f16 only where it is cheap, unless the user forced it.
                if (forced is null && type == "f16" && ctx > 8192)
                {
                    continue;
                }

                var swaTokens = Math.Min(ctx, (model.SlidingWindow + usedUbatch + 255) / 256 * 256);
                var kv = ((KvBytesPerToken(model, type) * ctx) + (model.SlidingWindowLayers * KvBytesPerLayerToken(model, type) * swaTokens)) / GiB;
                var total = weights + kv + stateGib + reserve;
                if (total <= budgetGib)
                {
                    var decision = string.IsNullOrEmpty(model.Decision) ? string.Empty : $"decision model ({model.Decision}), ";
                    var images = projectorBytes > 0 ? ", images" : string.Empty;
                    var why = $"{decision}{ctx / 1024}k context, {type} cache{images}: {OneDecimal(total)} of {OneDecimal(budgetGib)} GiB";
                    return new ModelPlan(true, ctx, type, total, budgetGib, why, usedUbatch, reserve);
                }
            }
        }

        var bare = weights + stateGib + reserve;
        return new ModelPlan(
            false,
            0,
            string.Empty,
            bare,
            budgetGib,
            $"needs {OneDecimal(bare)} GiB with a 4k context; {OneDecimal(budgetGib)} GiB is free",
            usedUbatch,
            reserve);
    }

    /// <summary>Bytes of KV cache per token of context for the full-attention layers. Sliding-window layers are separate.</summary>
    public static double KvBytesPerToken(GgufModelInfo model, string type)
    {
        var fullLayers = (long)model.KeyValueLayers - model.SlidingWindowLayers;
        return fullLayers * KvBytesPerLayerToken(model, type);
    }

    private static double KvBytesPerLayerToken(GgufModelInfo model, string type)
    {
        var perElement = type == "f16" ? 2.0 : type == "q8_0" ? 34.0 / 32 : 18.0 / 32;
        return model.KeyValueHeads * ((double)model.KeyLength + model.ValueLength) * perElement;
    }

    private static uint PresetCap(GgufModelInfo model, uint ctxCap)
    {
        if (!string.IsNullOrEmpty(model.Decision))
        {
            return ctxCap;
        }

        var haystack = $"{model.FileName} {model.Name} {model.Architecture}".ToLowerInvariant();
        foreach (var (match, maxContext) in Presets)
        {
            if (!haystack.Contains(match, StringComparison.Ordinal))
            {
                continue;
            }

            return maxContext > 0 ? Math.Min(ctxCap, maxContext) : ctxCap;
        }

        return ctxCap;
    }

    private static string OneDecimal(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}

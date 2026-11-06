using System.Globalization;

namespace PS5LM.Companion.Core.Models.Planning;

public enum RepoFitKind
{
    LikelyFits,
    LowBitOnly,
    TooBig,
}

/// <summary>
/// A rough hint for a repository, from its parameter count. It is not the file verdict: the same repository
/// holds several quants, and only a file's header says whether that file loads.
/// </summary>
/// <param name="Label">Short text for a chip: "likely fits", "low-bit only" or "too big".</param>
public sealed record RepoFit(RepoFitKind Kind, string Label, string Detail)
{
    /// <summary>About a Q2_K quant, the smallest common size the hint treats as loadable.</summary>
    public const double SmallestBitsPerWeight = 2.7;

    /// <summary>About a Q4 quant. A repository that fits at this size is "likely fits".</summary>
    public const double ComfortableBitsPerWeight = 4.5;

    public static RepoFit? Estimate(long? parameters, string? architecture, double budgetGib)
    {
        if (parameters is not > 0 || budgetGib <= 0 || double.IsNaN(budgetGib))
        {
            return null;
        }

        var smallest = WeightsGib(parameters.Value, SmallestBitsPerWeight) + ModelPlanner.WeightReserveGib;
        var comfortable = WeightsGib(parameters.Value, ComfortableBitsPerWeight) + ModelPlanner.WeightReserveGib;
        var family = string.IsNullOrWhiteSpace(architecture) ? string.Empty : architecture.Trim() + ". ";
        if (smallest > budgetGib)
        {
            return new RepoFit(
                RepoFitKind.TooBig,
                "too big",
                $"{family}About {GiB(smallest)} GiB at {Bits(SmallestBitsPerWeight)} bits per weight plus the reserve, more than the {GiB(budgetGib)} GiB free.");
        }

        if (comfortable > budgetGib)
        {
            return new RepoFit(
                RepoFitKind.LowBitOnly,
                "low-bit only",
                $"{family}A {Bits(ComfortableBitsPerWeight)}-bit quant is about {GiB(comfortable)} GiB. A {Bits(SmallestBitsPerWeight)}-bit quant is about {GiB(smallest)} GiB and can fit in the {GiB(budgetGib)} GiB free.");
        }

        return new RepoFit(
            RepoFitKind.LikelyFits,
            "likely fits",
            $"{family}A {Bits(ComfortableBitsPerWeight)}-bit quant is about {GiB(comfortable)} GiB of the {GiB(budgetGib)} GiB free. The file check is the verdict.");
    }

    private static double WeightsGib(long parameters, double bits) => parameters * bits / 8.0 / ModelPlanner.GiB;

    private static string GiB(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Bits(double bits) => bits.ToString("0.0", CultureInfo.InvariantCulture);
}

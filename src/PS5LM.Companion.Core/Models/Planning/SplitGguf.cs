using System.Text.RegularExpressions;

using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Core.Models.Planning;

/// <summary>Listed weight bytes, and whether they are the whole model.</summary>
/// <param name="Bytes">For an incomplete split, only the parts that are listed.</param>
/// <param name="Complete">False when this is the first part of a split and a declared part is missing.</param>
internal readonly record struct SplitWeight(long Bytes, bool Complete);

/// <summary>A GGUF split into <c>name-00001-of-00003.gguf</c> parts. The first part stands for the whole model.</summary>
internal static partial class SplitGguf
{
    public static bool TryParse(string name, out string prefix, out int part, out int count)
    {
        var match = Pattern().Match(name);
        if (!match.Success
            || !int.TryParse(match.Groups["part"].ValueSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out part)
            || !int.TryParse(match.Groups["count"].ValueSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count)
            || part <= 0
            || count <= 1)
        {
            prefix = string.Empty;
            part = 0;
            count = 0;
            return false;
        }

        prefix = match.Groups["prefix"].Value;
        return true;
    }

    public static bool IsLaterPart(string name) => TryParse(name, out _, out var part, out _) && part != 1;

    /// <summary>
    /// The weights to plan against. A split's first part sums the listed parts of that series, and
    /// <see cref="SplitWeight.Complete"/> is false when a declared part is missing, so the sum is only a lower bound.
    /// Any other file is its own size.
    /// </summary>
    public static SplitWeight WeightBytes(HubFile file, IReadOnlyList<HubFile> files)
    {
        if (!TryParse(file.Name, out var prefix, out var part, out var count) || part != 1)
        {
            return new SplitWeight(file.SizeBytes, true);
        }

        long sum = 0;
        var found = false;
        var parts = new HashSet<int>();
        foreach (var other in files)
        {
            if (!TryParse(other.Name, out var otherPrefix, out var otherPart, out var otherCount)
                || otherCount != count
                || !string.Equals(otherPrefix, prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sum += other.SizeBytes;
            found = true;
            if (otherPart <= count)
            {
                parts.Add(otherPart);
            }
        }

        // The only way to see this many distinct part numbers in 1..count is to have every declared part.
        return found ? new SplitWeight(sum, parts.Count == count) : new SplitWeight(file.SizeBytes, false);
    }

    [GeneratedRegex(@"^(?<prefix>.+)-(?<part>\d+)-of-(?<count>\d+)\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

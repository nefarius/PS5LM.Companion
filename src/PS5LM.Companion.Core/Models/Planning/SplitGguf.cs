using System.Text.RegularExpressions;

using PS5LM.Companion.Core.Transfers;

namespace PS5LM.Companion.Core.Models.Planning;

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

    /// <summary>The first part's weights are the sum of the parts in <paramref name="files"/>. Any other file is its own size.</summary>
    public static long WeightBytes(HubFile file, IReadOnlyList<HubFile> files)
    {
        if (!TryParse(file.Name, out var prefix, out var part, out var count) || part != 1)
        {
            return file.SizeBytes;
        }

        long sum = 0;
        var found = false;
        foreach (var other in files)
        {
            if (!TryParse(other.Name, out var otherPrefix, out _, out var otherCount)
                || otherCount != count
                || !string.Equals(otherPrefix, prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sum += other.SizeBytes;
            found = true;
        }

        return found ? sum : file.SizeBytes;
    }

    [GeneratedRegex(@"^(?<prefix>.+)-(?<part>\d+)-of-(?<count>\d+)\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}

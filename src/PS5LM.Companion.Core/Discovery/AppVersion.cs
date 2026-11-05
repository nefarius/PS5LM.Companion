using System.Globalization;
using System.Text.Json;

using PS5LM.Companion.Core.Updates;

namespace PS5LM.Companion.Core.Discovery;

/// <summary>
/// The installed PS5LM app's version. <c>sce_sys/param.json</c> stores it as a content version
/// (<c>01.011.000</c> is 0.11.0: the first number is one more than the major, then the minor and the patch).
/// Manifest pins use a tag such as <c>v0.11.0</c>.
/// </summary>
public static class AppVersion
{
    /// <summary>v0.11.0, when PS5LM started keeping its files in the app's own storage.</summary>
    public static SemanticVersion OwnStorage { get; } = Parsed("0.11.0");

    /// <summary>Parses a content version such as <c>01.009.001</c> (0.9.1) or <c>01.011.000</c> (0.11.0).</summary>
    public static bool TryParseContentVersion(string? raw, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var parts = raw.Trim().Split('.');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var encodedMajor)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch)
            || encodedMajor < 1
            || minor < 0
            || patch < 0)
        {
            return false;
        }

        // SemanticVersion rejects the leading zeros of a content version, so compare the decoded triple.
        return SemanticVersion.TryParse($"{encodedMajor - 1}.{minor}.{patch}", out version);
    }

    /// <summary>Parses a manifest tag such as <c>v0.11.0</c>.</summary>
    public static bool TryParseTag(string? raw, out SemanticVersion version) => SemanticVersion.TryParse(raw, out version);

    /// <summary>Reads <c>contentVersion</c> from an app's <c>param.json</c>.</summary>
    public static bool TryParseParam(string? paramJson, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(paramJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(paramJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("contentVersion", out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return TryParseContentVersion(value.GetString(), out version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary><c>0.11.0</c>, without a leading <c>v</c> and without build metadata.</summary>
    public static string Format(SemanticVersion version) =>
        string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}", version.Major, version.Minor, version.Patch);

    private static SemanticVersion Parsed(string text)
    {
        if (!SemanticVersion.TryParse(text, out var version))
        {
            throw new InvalidOperationException($"'{text}' is not an app version.");
        }

        return version;
    }
}

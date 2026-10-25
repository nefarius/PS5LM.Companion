using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PS5LM.Companion.Core.Setup;

/// <summary>An artifact that is placed on the console over FTP rather than through Payload Manager.</summary>
/// <param name="Url">Direct download URL.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the download.</param>
/// <param name="Target">Absolute path on the console.</param>
public sealed record ManifestFile(string Url, string Sha256, string Target);

/// <summary>How setup decides that a payload is up. Either a listening port or any of the named processes.</summary>
public sealed record ReadyCheck(int Port = 0, IReadOnlyList<string>? Processes = null);

/// <summary>One payload that setup uploads, autoloads and launches.</summary>
/// <param name="Id">Stable id; the payload is uploaded as <c>{Id}.elf</c>.</param>
/// <param name="DelayAfterMs">Autoload pause (and manual wait) after this payload, for heavy ones.</param>
/// <param name="ReadyTimeoutSeconds">How long to wait for <see cref="Ready"/> after launching.</param>
/// <param name="Optional">When true, a payload that is not up at the end is reported as a warning instead of a failure.</param>
/// <param name="OptionalNote">Shown with the warning to explain why the payload may legitimately not start.</param>
public sealed record ManifestPayload(
    string Id,
    string Name,
    string Version,
    string Url,
    string Sha256,
    ReadyCheck Ready,
    int DelayAfterMs = 0,
    int ReadyTimeoutSeconds = 30,
    IReadOnlyList<ManifestFile>? Files = null,
    bool Optional = false,
    string? OptionalNote = null)
{
    public string UploadName => Id + ".elf";
}

/// <summary>Payload Manager itself, with the oldest version setup can drive.</summary>
public sealed record ManifestPayloadManager(string Version, string MinimumVersion, string FileName, string Url, string Sha256);

/// <summary>A PS5LM variant that ShadowMountPlus installs from <c>/data/homebrew/{TitleId}</c>.</summary>
public sealed record ManifestApp(string Id, string Name, string TitleId, string Version, string Url, string Sha256);

/// <summary>The pinned set of downloads and how to use them.</summary>
public sealed record SetupManifest(
    ManifestPayloadManager PayloadManager,
    IReadOnlyList<ManifestPayload> Payloads,
    IReadOnlyList<ManifestApp> Apps)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Reads and validates a manifest; throws <see cref="InvalidDataException"/> with the first problem.</summary>
    public static SetupManifest Parse(string json)
    {
        SetupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SetupManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The setup manifest is not valid JSON: {ex.Message}", ex);
        }

        if (manifest?.PayloadManager is null || manifest.Payloads is null || manifest.Apps is null)
        {
            throw new InvalidDataException("The setup manifest needs payloadManager, payloads and apps.");
        }

        manifest.Validate();
        return manifest;
    }

    /// <summary>The manifest compiled into the assembly.</summary>
    public static SetupManifest LoadEmbedded()
    {
        var assembly = typeof(SetupManifest).Assembly;
        using var stream = assembly.GetManifestResourceStream("PS5LM.Companion.Core.Setup.setup-manifest.json")
            ?? throw new InvalidOperationException("The embedded setup manifest is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Every download in the manifest, for <c>--verify-manifest</c>.</summary>
    public IEnumerable<(string Label, string Url, string Sha256)> Artifacts()
    {
        yield return ("Payload Manager", PayloadManager.Url, PayloadManager.Sha256);
        foreach (var payload in Payloads)
        {
            yield return (payload.Name, payload.Url, payload.Sha256);
            foreach (var file in payload.Files ?? [])
            {
                yield return ($"{payload.Name} {file.Target}", file.Url, file.Sha256);
            }
        }

        foreach (var app in Apps)
        {
            yield return (app.Name, app.Url, app.Sha256);
        }
    }

    private void Validate()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in Payloads)
        {
            if (string.IsNullOrWhiteSpace(payload.Id) || !ids.Add(payload.Id))
            {
                throw new InvalidDataException($"Payload id '{payload.Id}' is empty or duplicated.");
            }

            if (payload.Ready is null || (payload.Ready.Port <= 0 && (payload.Ready.Processes is null || payload.Ready.Processes.Count == 0)))
            {
                throw new InvalidDataException($"Payload '{payload.Id}' needs a ready port or process list.");
            }

            CheckArtifact(payload.Id, payload.Url, payload.Sha256);
            foreach (var file in payload.Files ?? [])
            {
                CheckArtifact(payload.Id + " file", file.Url, file.Sha256);
                if (file.Target is not { Length: > 1 } || !file.Target.StartsWith("/data/", StringComparison.Ordinal) || file.Target.Contains("..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Payload '{payload.Id}' file target '{file.Target}' must be an absolute path under /data/.");
                }
            }
        }

        CheckArtifact("payloadManager", PayloadManager.Url, PayloadManager.Sha256);
        if (!PayloadManagerVersion.TryParse(PayloadManager.Version, out _) || !PayloadManagerVersion.TryParse(PayloadManager.MinimumVersion, out _))
        {
            throw new InvalidDataException("payloadManager version and minimumVersion must be numeric.");
        }

        if (string.IsNullOrEmpty(PayloadManager.FileName))
        {
            throw new InvalidDataException("payloadManager.fileName is required.");
        }

        if (!PayloadManager.FileName.Contains("_v", StringComparison.Ordinal) || PayloadManager.FileName.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidDataException("payloadManager.fileName must keep the _vX.Y.Z suffix Payload Manager uses to find updates.");
        }

        var appIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in Apps)
        {
            if (!appIds.Add(app.Id))
            {
                throw new InvalidDataException($"App id '{app.Id}' is duplicated.");
            }

            CheckArtifact(app.Id, app.Url, app.Sha256);
            if (app.TitleId is not { Length: 9 })
            {
                throw new InvalidDataException($"App '{app.Id}' needs a nine character title id.");
            }
        }
    }

    private static void CheckArtifact(string what, string url, string sha256)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException($"{what}: url must be an absolute https URL.");
        }

        if (sha256 is not { Length: 64 } || !sha256.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new InvalidDataException($"{what}: sha256 must be 64 lower-case hex characters.");
        }
    }
}

/// <summary>Payload Manager versions are plain dotted numbers, optionally prefixed with <c>v</c>.</summary>
public static class PayloadManagerVersion
{
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < trimmed.Length && (char.IsAsciiDigit(trimmed[end]) || trimmed[end] == '.'))
        {
            end++;
        }

        return Version.TryParse(trimmed[..end], out var parsed) && parsed is not null && (version = parsed) is not null;
    }
}

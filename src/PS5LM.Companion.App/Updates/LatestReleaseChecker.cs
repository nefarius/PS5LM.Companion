using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PS5LM.Companion.App.Updates;

/// <summary>
/// Compares the running assembly with the latest stable GitHub Release.
/// Network and payload failures leave the local version visible and do not report an update.
/// </summary>
internal sealed class LatestReleaseChecker : IDisposable
{
    public const string HttpClientName = "GitHubReleases";

    public static readonly Uri LatestReleaseUri = new(
        "https://api.github.com/repos/nefarius/PS5LM.Companion/releases/latest");

    public static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromHours(1);

    private readonly HttpClient _httpClient;
    private readonly IAssemblyVersionSource _versions;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cacheLifetime;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _cacheGate = new();
    private VersionCheckStatus? _cached;
    private DateTimeOffset _cachedAt;

    public LatestReleaseChecker(
        HttpClient httpClient,
        IAssemblyVersionSource versions,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(versions);
        if (cacheLifetime is { } lifetime && lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheLifetime));
        }

        _httpClient = httpClient;
        _versions = versions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheLifetime = cacheLifetime ?? DefaultCacheLifetime;
    }

    public static void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PS5LM.Companion");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    /// <summary>Returns the running version without contacting GitHub.</summary>
    public VersionCheckStatus GetLocalStatus() => LocalOnly(TryReadLocalVersion());

    /// <summary>
    /// Returns the cached comparison, fetching the latest stable release when the cache is empty or expired.
    /// </summary>
    public async Task<VersionCheckStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var cached = TryGetCached();
        if (cached is not null)
        {
            return cached;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cached = TryGetCached();
            if (cached is not null)
            {
                return cached;
            }

            var status = await FetchAsync(cancellationToken);
            Store(status);
            return status;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _refreshLock.Dispose();
    }

    private async Task<VersionCheckStatus> FetchAsync(CancellationToken cancellationToken)
    {
        var localVersion = TryReadLocalVersion();
        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseUri, cancellationToken);
            response.EnsureSuccessStatusCode();

            var release = await response.Content.ReadFromJsonAsync<GitHubLatestRelease>(cancellationToken);
            if (release is null
                || release.Draft
                || release.Prerelease
                || string.IsNullOrWhiteSpace(release.TagName)
                || !SemanticVersion.IsNewer(release.TagName, localVersion))
            {
                return LocalOnly(localVersion);
            }

            return new VersionCheckStatus(localVersion, release.TagName.Trim(), true, TryHttps(release.HtmlUrl));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return LocalOnly(localVersion);
        }
    }

    private string? TryReadLocalVersion()
    {
        try
        {
            var version = _versions.TryGetVersion();
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private VersionCheckStatus? TryGetCached()
    {
        lock (_cacheGate)
        {
            if (_cached is null)
            {
                return null;
            }

            var age = _timeProvider.GetUtcNow() - _cachedAt;
            if (age < TimeSpan.Zero || age >= _cacheLifetime)
            {
                return null;
            }

            return _cached;
        }
    }

    private void Store(VersionCheckStatus status)
    {
        lock (_cacheGate)
        {
            _cached = status;
            _cachedAt = _timeProvider.GetUtcNow();
        }
    }

    private static Uri? TryHttps(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return uri;
    }

    private static VersionCheckStatus LocalOnly(string? localVersion) => new(localVersion, null, false, null);

    private sealed class GitHubLatestRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }
}

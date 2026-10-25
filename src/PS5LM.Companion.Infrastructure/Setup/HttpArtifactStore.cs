using System.Security.Cryptography;

using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.Infrastructure.Setup;

/// <summary>
/// Downloads artifacts into a cache directory. A cached file is reused only when its SHA-256 still matches;
/// a download that does not match the pinned hash is deleted and reported.
/// </summary>
public sealed class HttpArtifactStore(HttpClient http, string cacheDirectory) : IArtifactStore
{
    public async Task<string> FetchAsync(string url, string sha256, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        var path = Path.Combine(cacheDirectory, $"{sha256[..12]}-{name}");
        if (File.Exists(path) && await HashAsync(path, cancellationToken) == sha256)
        {
            return path;
        }

        var part = path + ".part";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(part);
            await source.CopyToAsync(target, cancellationToken);
        }

        var actual = await HashAsync(part, cancellationToken);
        if (actual != sha256)
        {
            File.Delete(part);
            throw new InvalidDataException($"{url} has SHA-256 {actual}, expected {sha256}.");
        }

        File.Move(part, path, overwrite: true);
        return path;
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}

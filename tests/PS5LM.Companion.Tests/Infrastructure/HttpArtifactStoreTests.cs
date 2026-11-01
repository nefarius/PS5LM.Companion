using System.Net;
using System.Security.Cryptography;
using System.Text;

using PS5LM.Companion.Infrastructure.Setup;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class HttpArtifactStoreTests
{
    [Fact]
    public async Task Concurrent_fetches_of_the_same_pin_finish_on_one_cache_file()
    {
        var bytes = Encoding.UTF8.GetBytes("pinned-artifact");
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var cache = Directory.CreateTempSubdirectory("artifact-cache").FullName;
        try
        {
            using var http = new HttpClient(new SlowHandler(bytes), disposeHandler: true);
            var store = new HttpArtifactStore(http, cache);

            var paths = await Task.WhenAll(
                store.FetchAsync("https://example.test/pin.bin", sha, CancellationToken.None),
                store.FetchAsync("https://example.test/pin.bin", sha, CancellationToken.None));

            Assert.Equal(paths[0], paths[1]);
            Assert.Empty(Directory.GetFiles(cache, "*.part"));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(paths[0]));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    private sealed class SlowHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(50, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
}

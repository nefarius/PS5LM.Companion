using System.Net;
using System.Text;

using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Tests.Support;

namespace PS5LM.Companion.Tests.Contracts;

/// <summary>WebsrvClient against the response shapes from ps5-payload-dev/websrv.</summary>
public sealed class WebsrvContractTests
{
    [Fact]
    public async Task Probe_reads_the_version_tag_and_the_title_directories()
    {
        var snapshot = await new WebsrvClient(Client()).ProbeAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Equal("v0.0", snapshot.Version);
        Assert.Contains("PPSA99575", snapshot.InstalledTitleIds);
        Assert.Contains("PPSA99581", snapshot.InstalledTitleIds);
        Assert.False(snapshot.RunningKnown);
    }

    [Fact]
    public async Task Launch_maps_the_recorded_503_to_a_refusal()
    {
        var result = await new WebsrvClient(Client()).LaunchAsync("PPSA99575", CancellationToken.None);

        Assert.True(result.Dispatched);
        Assert.False(result.Accepted);
        Assert.Contains("refused", result.Message, StringComparison.Ordinal);
    }

    private static HttpClient Client() => new(new WebsrvHandler()) { BaseAddress = new Uri("http://websrv.test/") };

    private sealed class WebsrvHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/version")
            {
                return Task.FromResult(Json(FixtureFiles.Read("websrv", "source", "version.json")));
            }

            if (path.StartsWith("/fs/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(FixtureFiles.Read("websrv", "source", "fs.json")));
            }

            if (path == "/launch")
            {
                var status = (HttpStatusCode)int.Parse(FixtureFiles.Read("websrv", "source", "launch-503.status").Trim(), System.Globalization.CultureInfo.InvariantCulture);
                return Task.FromResult(new HttpResponseMessage(status));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

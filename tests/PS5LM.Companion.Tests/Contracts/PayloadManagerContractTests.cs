using System.Net;
using System.Text;

using PS5LM.Companion.Infrastructure.Http;
using PS5LM.Companion.Infrastructure.Setup;
using PS5LM.Companion.Tests.Support;

using Refit;

namespace PS5LM.Companion.Tests.Contracts;

/// <summary>The real Payload Manager clients against responses recorded per upstream version.</summary>
public sealed class PayloadManagerContractTests
{
    [Theory]
    [InlineData("0.6.3")]
    [InlineData("0.5.2")]
    public async Task Version_text_parses(string version)
    {
        Assert.Equal(version, await Admin(version).GetVersionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Version_06_lists_slots_and_05_lists_paths()
    {
        var current = await Admin("0.6.3").ListPayloadsAsync(CancellationToken.None);
        var legacy = await Admin("0.5.2").ListPayloadsAsync(CancellationToken.None);

        Assert.Contains(current, payload => payload.Slot == "local/elfldr" && payload.FileName == "elfldr.elf");
        Assert.Contains(legacy, payload => payload.Slot == string.Empty && payload.FileName == "elfldr.elf");
    }

    [Fact]
    public async Task Version_06_config_and_processes_round_trip()
    {
        string? posted = null;
        var admin = Admin("0.6.3", body => posted = body);

        var config = await admin.GetAutoloadAsync(CancellationToken.None);
        Assert.NotNull(config);
        Assert.True(config.Enabled);
        Assert.Contains("local/elfldr", config.List, StringComparison.Ordinal);

        await admin.SetAutoloadAsync(true, config.List, CancellationToken.None);
        Assert.Contains("local/elfldr", posted, StringComparison.Ordinal);

        var processes = await admin.GetProcessesAsync(CancellationToken.None);
        Assert.Contains("kstuff.elf", processes!);
        Assert.Contains("onion_daemon.elf", processes!);
    }

    [Fact]
    public async Task Version_06_rejects_a_mutating_get_and_accepts_post()
    {
        var http = Http("0.6.3");
        using (var get = await http.GetAsync("/loadpayload:local/elfldr", CancellationToken.None))
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        }

        var launched = await Admin(http).LoadAsync("local/elfldr", CancellationToken.None);
        Assert.True(launched.Accepted);
    }

    [Fact]
    public async Task Version_05_still_allows_a_mutating_get()
    {
        using var get = await Http("0.5.2").GetAsync("/loadpayload:elfldr", CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task Catalog_reads_06_process_names_and_skips_items_without_an_id()
    {
        var snapshot = await new PayloadManagerClient(RestService.For<IPayloadManagerApi>(Http("0.6.3"))).ReadAsync(CancellationToken.None);

        Assert.True(snapshot.Available);
        Assert.Empty(snapshot.Installed);
        Assert.Contains("kstuff.elf", snapshot.RunningProcesses!);
    }

    private static PayloadManagerAdminClient Admin(string version, Action<string>? posted = null) => Admin(Http(version, posted));

    private static PayloadManagerAdminClient Admin(HttpClient http) => new(http);

    private static HttpClient Http(string version, Action<string>? posted = null) =>
        new(new FixtureHandler(version, posted)) { BaseAddress = new Uri("http://pld.test/") };

    private sealed class FixtureHandler(string version, Action<string>? posted) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("loadpayload", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Text(HttpStatusCode.OK, "OK");
                }

                var status = (HttpStatusCode)int.Parse(FixtureFiles.Read("payload-manager", version, "load-get.status").Trim(), System.Globalization.CultureInfo.InvariantCulture);
                return Text(status, FixtureFiles.Read("payload-manager", version, "load-get.body"));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("set_config", StringComparison.Ordinal))
            {
                posted?.Invoke(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Text(HttpStatusCode.OK, "OK");
            }

            var name = path.Trim('/').Replace('/', '_') + (path.Contains("version", StringComparison.Ordinal) ? ".txt" : ".json");
            var file = Path.Combine(AppContext.BaseDirectory, "Fixtures", "payload-manager", version, name);
            if (!File.Exists(file))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var media = name.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "text/plain";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(File.ReadAllText(file), Encoding.UTF8, media),
            };
        }

        private static HttpResponseMessage Text(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
    }
}

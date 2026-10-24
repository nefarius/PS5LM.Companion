using Microsoft.Extensions.Logging;

using PS5LM.Companion.Core.Consoles;
using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Infrastructure.Ftp;
using PS5LM.Companion.Infrastructure.Http;

using Refit;

namespace PS5LM.Companion.Infrastructure;

/// <summary>
/// Builds the FTP, HTTP and TCP clients for one console. The pooled <see cref="HttpClient"/> pipelines (with their
/// timeouts) are shared, and only the base address differs per console.
/// </summary>
public sealed class ConsoleConnectorFactory(IHttpClientFactory httpClients, ILoggerFactory loggers) : IConsoleConnectorFactory
{
    public const string LlamaClient = "llama-probe";
    public const string ExporterClient = "exporter-probe";
    public const string PayloadManagerClient = "payload-manager";

    public ConsoleConnectors Create(string consoleId, PS5Options options)
    {
        var llama = RestService.For<ILlamaServerApi>(Client(LlamaClient, new UriBuilder(Uri.UriSchemeHttp, options.Host, options.LlamaPort).Uri));
        var exporter = RestService.For<IPS5ExporterApi>(Client(ExporterClient, new UriBuilder(Uri.UriSchemeHttp, options.Host, options.ExporterPort).Uri));
        var manager = new Http.PayloadManagerClient(RestService.For<IPayloadManagerApi>(Client(PayloadManagerClient, options.PayloadManagerBaseAddress())));

        return new ConsoleConnectors(
            new FtpFileSystem(options, loggers.CreateLogger<FtpFileSystem>()),
            new LlamaServerProbe(llama),
            new ExporterSensorProbe(exporter),
            manager,
            manager,
            new TcpElfldrProbe(options));
    }

    private HttpClient Client(string name, Uri baseAddress)
    {
        var client = httpClients.CreateClient(name);
        client.BaseAddress = baseAddress;
        return client;
    }
}

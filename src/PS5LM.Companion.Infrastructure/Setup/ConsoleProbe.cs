using System.Net.Sockets;

using PS5LM.Companion.Core.Setup;

namespace PS5LM.Companion.Infrastructure.Setup;

/// <summary>TCP and HTTP checks against one console host.</summary>
public sealed class ConsoleProbe(string host, HttpClient http) : IConsoleProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public async Task<bool> PortOpenAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<bool> HtmlAsync(int port, string path, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new UriBuilder(Uri.UriSchemeHttp, host, port, path).Uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not null && type.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return (await response.Content.ReadAsStringAsync(cancellationToken)).TrimStart().StartsWith('<');
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>Streams an ELF to elfldr's port; elfldr runs it when the connection closes.</summary>
public sealed class ElfldrSender(string host, int port) : IElfldrSender
{
    public async Task SendAsync(string filePath, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, cancellationToken);
        await using var stream = client.GetStream();
        await using var file = File.OpenRead(filePath);
        await file.CopyToAsync(stream, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        client.Client.Shutdown(SocketShutdown.Send);
    }
}

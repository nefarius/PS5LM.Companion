using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PS5LM.Companion.Tests.Support;

/// <summary>
/// Speaks the ftpsrv replies captured in <c>Fixtures/ftpsrv</c>: MLSD ignores a path argument and lists the working
/// directory, an empty directory lists only <c>.</c> and <c>..</c>, and <c>DELE</c> answers 226.
/// </summary>
internal sealed class FtpsrvStandIn : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    private FtpsrvStandIn(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accepting = AcceptAsync(_stop.Token);
    }

    public int Port { get; }

    public static FtpsrvStandIn Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FtpsrvStandIn(listener);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => SessionAsync(client), CancellationToken.None);
        }
    }

    private static async Task SessionAsync(TcpClient client)
    {
        using var _ = client;
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        var cwd = "/";
        TcpListener? passive = null;
        await writer.WriteLineAsync("220-Welcome to ftpsrv.elf");
        await writer.WriteLineAsync("220-Version: v0.21.1");
        await writer.WriteLineAsync("220 Service is ready");

        while (await reader.ReadLineAsync() is { } line)
        {
            var split = line.Split(' ', 2);
            var verb = split[0].ToUpperInvariant();
            var argument = split.Length > 1 ? split[1] : string.Empty;
            switch (verb)
            {
                case "USER":
                case "PASS":
                    await writer.WriteLineAsync("230 User logged in");
                    break;
                case "FEAT":
                    await writer.WriteLineAsync("211-Features:");
                    await writer.WriteLineAsync(" MLSD");
                    await writer.WriteLineAsync("211 End");
                    break;
                case "SYST":
                    await writer.WriteLineAsync("215 UNIX Type: L8");
                    break;
                case "PWD":
                    await writer.WriteLineAsync($"257 \"{cwd}\"");
                    break;
                case "CWD":
                    cwd = argument;
                    await writer.WriteLineAsync("250 OK");
                    break;
                case "PASV":
                    passive?.Stop();
                    passive = new TcpListener(IPAddress.Loopback, 0);
                    passive.Start();
                    var port = ((IPEndPoint)passive.LocalEndpoint).Port;
                    await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256}).");
                    break;
                case "MLSD":
                    // A path argument is ignored: the listing is always the working directory.
                    var listing = cwd == "/data/shadowmount" ? "mlsd-shadowmount.txt"
                        : cwd == "/data/OnionHEN/games" ? "mlsd-empty.txt"
                        : "mlsd-ignored-path.txt";
                    var body = FixtureFiles.ReadBytes("ftpsrv", "0.21.1", listing);
                    if (passive is null)
                    {
                        await writer.WriteLineAsync("425 No data connection");
                        break;
                    }

                    await writer.WriteLineAsync("150 Opening data transfer");
                    using (var data = await passive.AcceptTcpClientAsync())
                    await using (var dataStream = data.GetStream())
                    {
                        await dataStream.WriteAsync(body);
                    }

                    passive.Stop();
                    passive = null;
                    await writer.WriteLineAsync("226 Transfer complete");
                    break;
                case "DELE":
                case "RMD":
                    await writer.WriteLineAsync("226 File deleted");
                    break;
                case "RNFR":
                    await writer.WriteLineAsync("350 Ready");
                    break;
                case "RNTO":
                    await writer.WriteLineAsync("226 Rename successful");
                    break;
                case "QUIT":
                    await writer.WriteLineAsync("221 Goodbye");
                    return;
                case "SIZE":
                case "MDTM":
                case "NLST":
                case "LIST":
                case "MLST":
                case "EPSV":
                case "AUTH":
                    await writer.WriteLineAsync("502 Command not implemented");
                    break;
                default:
                    await writer.WriteLineAsync("200 OK");
                    break;
            }
        }
    }
}

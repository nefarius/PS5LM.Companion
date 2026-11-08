using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using PS5LM.Companion.Core.Discovery;
using PS5LM.Companion.Infrastructure.Ftp;

namespace PS5LM.Companion.Tests.Infrastructure;

/// <summary>
/// The two FTP lanes against a server that can hold one command open. The stand-in speaks just enough FTP for
/// FluentFTP to log in, list and upload.
/// </summary>
public sealed class FtpLaneTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_listing_completes_while_an_upload_holds_the_bulk_lane()
    {
        await using var server = await GatedFtpServer.StartAsync(holdUploads: true);
        using var fs = Client(server.Port);
        await using var uploaded = new MemoryStream(new byte[64]);

        var upload = fs.UploadAsync(uploaded, "/data/PS5LM/models/m.gguf.part", null, CancellationToken.None);
        await WaitAsync(server.FirstUploadAccepted, server);

        var exists = await fs.FileExistsAsync("/missing/file", CancellationToken.None).WaitAsync(Bound);

        Assert.False(exists);
        server.ReleaseUploads();
        await upload.WaitAsync(Bound);
    }

    [Fact]
    public async Task Two_listings_run_one_at_a_time()
    {
        await using var server = await GatedFtpServer.StartAsync(holdListings: true);
        using var fs = Client(server.Port);

        var first = fs.FileExistsAsync("/missing/one", CancellationToken.None);
        await WaitAsync(server.FirstListing, server);
        var second = fs.FileExistsAsync("/missing/two", CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(1, server.Listings);
        server.ReleaseListings();
        await Task.WhenAll(first, second).WaitAsync(Bound);
        Assert.Equal(2, server.Listings);
    }

    [Fact]
    public async Task Two_uploads_run_one_at_a_time()
    {
        await using var server = await GatedFtpServer.StartAsync(holdUploads: true);
        using var fs = Client(server.Port);
        await using var first = new MemoryStream(new byte[64]);
        await using var secondStream = new MemoryStream(new byte[64]);

        var upload = fs.UploadAsync(first, "/data/PS5LM/models/a.gguf.part", null, CancellationToken.None);
        await WaitAsync(server.FirstUploadAccepted, server);
        var queued = fs.UploadAsync(secondStream, "/data/PS5LM/models/b.gguf.part", null, CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(1, server.UploadsAccepted);
        server.ReleaseUploads();
        await Task.WhenAll(upload, queued).WaitAsync(Bound);
        Assert.Equal(2, server.UploadsAccepted);
    }

    [Fact]
    public async Task A_listing_completes_while_a_download_holds_the_bulk_lane()
    {
        await using var server = await GatedFtpServer.StartAsync(holdDownloads: true);
        using var fs = Client(server.Port);
        await using var destination = new MemoryStream();

        var download = fs.DownloadToAsync("/data/PS5LM/models/m.gguf", destination, null, CancellationToken.None);
        await WaitAsync(server.FirstDownloadAccepted, server);

        var exists = await fs.FileExistsAsync("/missing/file", CancellationToken.None).WaitAsync(Bound);

        Assert.False(exists);
        server.ReleaseDownloads();
        await download.WaitAsync(Bound);
        Assert.Equal("file"u8.ToArray(), destination.ToArray());
    }

    [Fact]
    public async Task A_download_and_an_upload_run_one_at_a_time()
    {
        await using var server = await GatedFtpServer.StartAsync(holdDownloads: true);
        using var fs = Client(server.Port);
        await using var destination = new MemoryStream();
        await using var upload = new MemoryStream(new byte[64]);

        var download = fs.DownloadToAsync("/data/PS5LM/models/m.gguf", destination, null, CancellationToken.None);
        await WaitAsync(server.FirstDownloadAccepted, server);
        var queued = fs.UploadAsync(upload, "/data/PS5LM/models/n.gguf.part", null, CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(0, server.UploadsAccepted);
        server.ReleaseDownloads();
        await Task.WhenAll(download, queued).WaitAsync(Bound);
        Assert.Equal(1, server.DownloadsAccepted);
        Assert.Equal(1, server.UploadsAccepted);
    }

    [Fact]
    public async Task Two_downloads_run_one_at_a_time()
    {
        await using var server = await GatedFtpServer.StartAsync(holdDownloads: true);
        using var fs = Client(server.Port);
        await using var first = new MemoryStream();
        await using var second = new MemoryStream();

        var download = fs.DownloadToAsync("/data/PS5LM/models/a.gguf", first, null, CancellationToken.None);
        await WaitAsync(server.FirstDownloadAccepted, server);
        var queued = fs.DownloadToAsync("/data/PS5LM/models/b.gguf", second, null, CancellationToken.None);
        await Task.Delay(200);

        Assert.Equal(1, server.DownloadsAccepted);
        server.ReleaseDownloads();
        await Task.WhenAll(download, queued).WaitAsync(Bound);
        Assert.Equal(2, server.DownloadsAccepted);
    }

    private static async Task WaitAsync(Task signal, GatedFtpServer server)
    {
        try
        {
            await signal.WaitAsync(Bound);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"The server saw: {server.Transcript}", ex);
        }
    }

    private static FtpFileSystem Client(int port) =>
        new(new PS5Options { Host = "127.0.0.1", FtpPort = port }, NullLogger<FtpFileSystem>.Instance);

    /// <summary>Answers the commands FluentFTP sends and can pause the first listing or the first upload.</summary>
    private sealed class GatedFtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopped = new();
        private readonly Task _accepting;
        private readonly bool _holdListings;
        private readonly bool _holdUploads;
        private readonly bool _holdDownloads;
        private readonly TaskCompletionSource _releaseListings = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseUploads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDownloads = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstListing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstUpload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _commands = [];
        private int _listings;
        private int _uploads;
        private int _downloads;

        private GatedFtpServer(TcpListener listener, int port, bool holdListings, bool holdUploads, bool holdDownloads)
        {
            _listener = listener;
            Port = port;
            _holdListings = holdListings;
            _holdUploads = holdUploads;
            _holdDownloads = holdDownloads;
            _accepting = AcceptAsync();
        }

        public int Port { get; }

        public Task FirstListing => _firstListing.Task;

        public Task FirstUploadAccepted => _firstUpload.Task;

        public Task FirstDownloadAccepted => _firstDownload.Task;

        public int Listings => Volatile.Read(ref _listings);

        public int UploadsAccepted => Volatile.Read(ref _uploads);

        public int DownloadsAccepted => Volatile.Read(ref _downloads);

        public string Transcript
        {
            get
            {
                lock (_commands)
                {
                    return string.Join(' ', _commands);
                }
            }
        }

        public static Task<GatedFtpServer> StartAsync(bool holdListings = false, bool holdUploads = false, bool holdDownloads = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return Task.FromResult(new GatedFtpServer(listener, port, holdListings, holdUploads, holdDownloads));
        }

        public void ReleaseListings() => _releaseListings.TrySetResult();

        public void ReleaseUploads() => _releaseUploads.TrySetResult();

        public void ReleaseDownloads() => _releaseDownloads.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            ReleaseListings();
            ReleaseUploads();
            ReleaseDownloads();
            _stopped.Cancel();
            _listener.Stop();
            await _accepting;
        }

        private async Task AcceptAsync()
        {
            while (!_stopped.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopped.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
                TcpListener? passive = null;
                try
                {
                    await writer.WriteLineAsync("220 test");
                    while (await reader.ReadLineAsync(_stopped.Token) is { } line)
                    {
                        var split = line.IndexOf(' ');
                        var verb = (split < 0 ? line : line[..split]).ToUpperInvariant();
                        var argument = split < 0 ? string.Empty : line[(split + 1)..];
                        lock (_commands)
                        {
                            _commands.Add(verb);
                        }

                        switch (verb)
                        {
                            case "USER":
                                await writer.WriteLineAsync("331 ok");
                                break;
                            case "PASS":
                                await writer.WriteLineAsync("230 ok");
                                break;
                            case "FEAT":
                                await writer.WriteLineAsync("211-Features:");
                                await writer.WriteLineAsync("211 End");
                                break;
                            case "SYST":
                                await writer.WriteLineAsync("215 UNIX Type: L8");
                                break;
                            case "QUIT":
                                await writer.WriteLineAsync("221 bye");
                                return;
                            case "PASV":
                            case "EPSV":
                                passive = OpenPassive(passive, out var port);
                                await writer.WriteLineAsync(verb == "EPSV"
                                    ? $"229 Entering Extended Passive Mode (|||{port}|)"
                                    : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                                break;
                            case "CWD":
                            case "XCWD":
                                if (Interlocked.Increment(ref _listings) == 1)
                                {
                                    _firstListing.TrySetResult();
                                }

                                if (_holdListings)
                                {
                                    await _releaseListings.Task.WaitAsync(_stopped.Token);
                                }

                                await writer.WriteLineAsync("550 missing");
                                break;
                            case "SIZE":
                                await writer.WriteLineAsync("213 4");
                                break;
                            case "RETR":
                                await SendDataAsync(passive, writer, hold: _holdDownloads && DownloadsAccepted == 0, () =>
                                {
                                    if (Interlocked.Increment(ref _downloads) == 1)
                                    {
                                        _firstDownload.TrySetResult();
                                    }
                                });
                                break;
                            case "STOR":
                                await AcceptDataAsync(passive, writer, hold: _holdUploads && UploadsAccepted == 0, () =>
                                {
                                    if (Interlocked.Increment(ref _uploads) == 1)
                                    {
                                        _firstUpload.TrySetResult();
                                    }
                                });
                                break;
                            case "MLSD" or "LIST" or "NLST":
                                await AcceptDataAsync(passive, writer, hold: false, static () => { });
                                break;
                            case "MKD" or "XMKD":
                                await writer.WriteLineAsync($"257 \"{argument}\" created");
                                break;
                            case "PWD" or "XPWD":
                                await writer.WriteLineAsync("257 \"/\"");
                                break;
                            default:
                                await writer.WriteLineAsync("200 ok");
                                break;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
                {
                    // The client closed the connection, or the server is stopping.
                }
                finally
                {
                    passive?.Stop();
                }
            }
        }

        /// <summary>Replies 150, accepts the data connection, optionally waits, drains it and replies 226.</summary>
        private async Task AcceptDataAsync(TcpListener? passive, StreamWriter writer, bool hold, Action onAccepted)
        {
            if (passive is null)
            {
                await writer.WriteLineAsync("425 no passive port");
                return;
            }

            await writer.WriteLineAsync("150 go");
            using var data = await passive.AcceptTcpClientAsync(_stopped.Token);
            onAccepted();
            if (hold)
            {
                await _releaseUploads.Task.WaitAsync(_stopped.Token);
            }

            await using var incoming = data.GetStream();
            var buffer = new byte[8192];
            while (await incoming.ReadAsync(buffer, _stopped.Token) > 0)
            {
                // The bytes are irrelevant; the upload only has to finish.
            }

            await writer.WriteLineAsync("226 done");
        }

        /// <summary>Replies 150, accepts the data connection, writes a small payload and replies 226.</summary>
        private async Task SendDataAsync(TcpListener? passive, StreamWriter writer, bool hold, Action onAccepted)
        {
            if (passive is null)
            {
                await writer.WriteLineAsync("425 no passive port");
                return;
            }

            await writer.WriteLineAsync("150 go");
            using var data = await passive.AcceptTcpClientAsync(_stopped.Token);
            onAccepted();
            if (hold)
            {
                await _releaseDownloads.Task.WaitAsync(_stopped.Token);
            }

            await using var outgoing = data.GetStream();
            await outgoing.WriteAsync("file"u8.ToArray(), _stopped.Token);
            await writer.WriteLineAsync("226 done");
        }

        private static TcpListener OpenPassive(TcpListener? previous, out int port)
        {
            previous?.Stop();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return listener;
        }
    }
}

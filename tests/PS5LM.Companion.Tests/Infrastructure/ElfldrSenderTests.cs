using System.Net;
using System.Net.Sockets;

using PS5LM.Companion.Infrastructure.Setup;

namespace PS5LM.Companion.Tests.Infrastructure;

public sealed class ElfldrSenderTests
{
    [Fact]
    public async Task An_elfldr_that_keeps_the_connection_open_accepts()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var held = AcceptAndHoldAsync(listener, close: false);

        Assert.True(await new ElfldrSender("127.0.0.1", port).AcceptsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_elfldr_that_closes_the_connection_at_once_does_not_accept()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var held = AcceptAndHoldAsync(listener, close: true);

        Assert.False(await new ElfldrSender("127.0.0.1", port).AcceptsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_closed_port_does_not_accept()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.False(await new ElfldrSender("127.0.0.1", port).AcceptsAsync(CancellationToken.None));
    }

    private static CancellationTokenSource AcceptAndHoldAsync(TcpListener listener, bool close)
    {
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!close)
                {
                    await Task.Delay(Timeout.Infinite, stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });
        return stop;
    }
}

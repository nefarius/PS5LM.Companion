using System.Net.Sockets;

using PS5LM.Companion.Core.Abstractions;
using PS5LM.Companion.Core.Discovery;

namespace PS5LM.Companion.Infrastructure.Http;

/// <summary>A short TCP connect to elfldr. A closed port is a normal false, and the caller's cancellation still throws.</summary>
public sealed class TcpElfldrProbe(PS5Options options) : IElfldrProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<bool> IsListeningAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            await client.ConnectAsync(options.Host, options.ElfldrPort, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

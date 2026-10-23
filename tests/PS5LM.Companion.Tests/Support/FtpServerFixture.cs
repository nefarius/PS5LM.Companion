using System.Net;
using System.Net.Sockets;

using System.Security.Claims;

using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.FileSystem.DotNet;

using Microsoft.Extensions.DependencyInjection;

namespace PS5LM.Companion.Tests.Support;

/// <summary>A real FTP server over a temp folder, standing in for ftpsrv on the console.</summary>
internal sealed class FtpServerFixture : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly IFtpServerHost _host;

    private FtpServerFixture(ServiceProvider services, IFtpServerHost host, string root, int port)
    {
        _services = services;
        _host = host;
        Root = root;
        Port = port;
    }

    /// <summary>The folder the server exposes as "/".</summary>
    public string Root { get; }

    public int Port { get; }

    public static async Task<FtpServerFixture> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ps5lm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var services = new ServiceCollection()
            .AddLogging()
            .Configure<FtpServerOptions>(o =>
            {
                o.ServerAddress = "127.0.0.1";
                o.Port = port;
            })
            .Configure<DotNetFileSystemOptions>(o => o.RootPath = root)
            .AddSingleton<IMembershipProvider, AcceptEveryone>()
            .AddFtpServer(builder => builder.UseDotNetFileSystem())
            .BuildServiceProvider();

        var host = services.GetRequiredService<IFtpServerHost>();
        await host.StartAsync(CancellationToken.None);
        return new FtpServerFixture(services, host, root, port);
    }

    public string Local(string ftpPath) => Path.Combine(Root, ftpPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A lingering connection may still hold a file; the temp folder is cleaned by the OS.
        }
    }
}

/// <summary>ftpsrv on the console accepts any login; so does this stand-in.</summary>
internal sealed class AcceptEveryone : IMembershipProvider
{
    public Task<MemberValidationResult> ValidateUserAsync(string username, string password) =>
        Task.FromResult(new MemberValidationResult(
            MemberValidationStatus.AuthenticatedUser,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, username)], "test"))));
}

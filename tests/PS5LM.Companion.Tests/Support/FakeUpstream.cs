using System.Collections.Concurrent;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PS5LM.Companion.Tests.Support;

/// <summary>What a fake console's llama-server received.</summary>
internal sealed record UpstreamRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>A real HTTP listener standing in for one console's llama-server, so YARP forwards over a socket.</summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeUpstream(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public ConcurrentQueue<UpstreamRequest> Requests { get; } = new();

    public Func<HttpContext, string, Task>? Handler { get; set; }

    public static async Task<FakeUpstream> StartAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        var app = builder.Build();
        FakeUpstream? self = null;
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            self!.Requests.Enqueue(new UpstreamRequest(
                context.Request.Method,
                context.Request.Path,
                context.Request.QueryString.Value ?? string.Empty,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body));
            if (self.Handler is { } handler)
            {
                await handler(context, body);
                return;
            }

            context.Response.Headers["X-Upstream"] = name;
            await context.Response.WriteAsJsonAsync(new { upstream = name, path = context.Request.Path.Value });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new FakeUpstream(app, new Uri(address).Port);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

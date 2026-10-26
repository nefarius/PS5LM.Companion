using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

using PS5LM.Companion.Core.Inference;

using Yarp.ReverseProxy.Forwarder;

namespace PS5LM.Companion.App.Inference;

/// <summary>The upstream connection pool shared by every forwarded request. Retries and redirects are off on purpose.</summary>
public sealed class InferenceUpstream : IDisposable
{
    public InferenceUpstream()
    {
        Invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            EnableMultipleHttp2Connections = false,
        });
    }

    public HttpMessageInvoker Invoker { get; }

    public void Dispose() => Invoker.Dispose();
}

/// <summary>
/// Forwards the request to the console and drops the companion's own credentials on the way,
/// so the access token never reaches the PS5.
/// </summary>
internal sealed class GatewayTransformer : HttpTransformer
{
    public static GatewayTransformer Instance { get; } = new();

    public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
        proxyRequest.Headers.Remove("Authorization");
        proxyRequest.Headers.Remove("x-api-key");
        proxyRequest.Headers.Remove("Cookie");
        proxyRequest.Headers.Host = null;
    }
}

/// <summary>
/// The OpenAI, Anthropic and System One routes, spread over the consoles that serve the requested model alias.
/// Only request-scoped routes are forwarded; llama-server's admin and slot routes never are.
/// </summary>
internal static class InferenceGateway
{
    internal const string ConsoleHeader = "X-PS5LM-Console";
    internal const string PoolHeader = "X-PS5LM-Pool";

    private static readonly string[] ForwardedPaths =
    [
        "/chat/completions",
        "/completions",
        "/responses",
        "/messages",
        "/systemone",
        "/chat/completions/input_tokens",
        "/responses/input_tokens",
        "/messages/count_tokens",
    ];

    public static void MapInferenceGateway(this WebApplication app)
    {
        var group = app.MapGroup("/v1");
        group.MapGet("/models", ListModels);
        foreach (var path in ForwardedPaths)
        {
            group.MapPost(path, Forward);
        }
    }

    private static IResult ListModels(InferenceRouter router)
    {
        var data = router.Snapshot().Select(pool => new
        {
            id = pool.Alias,
            @object = "model",
            created = 0,
            owned_by = "ps5lm",
            ps5lm = new { replicas = pool.Replicas.Count, ready = pool.EligibleReplicas },
        });
        return Results.Json(new { @object = "list", data });
    }

    private static async Task Forward(
        HttpContext context,
        InferenceRouter router,
        InferenceUpstream upstream,
        IHttpForwarder forwarder,
        IOptions<InferenceOptions> options,
        ILogger<InferenceRouter> logger)
    {
        var settings = options.Value;
        var cancellation = context.RequestAborted;

        var read = await ReadModelAsync(context, settings.MaxRequestBodyMegabytes * 1024L * 1024L, cancellation);
        if (read.Error is not null)
        {
            await WriteError(context, read.Status, read.ErrorType!, read.Error);
            return;
        }

        var alias = read.Model ?? router.SoleAlias;
        if (alias is null)
        {
            await WriteError(context, StatusCodes.Status400BadRequest, "invalid_request_error", "The request has no 'model' and several models are served.");
            return;
        }

        var admission = await router.AcquireAsync(alias, cancellation);
        if (admission.Lease is not { } lease)
        {
            var (status, type) = admission.Failure switch
            {
                AdmissionFailure.UnknownModel => (StatusCodes.Status404NotFound, "model_not_found"),
                AdmissionFailure.QueueFull => (StatusCodes.Status429TooManyRequests, "rate_limit_error"),
                _ => (StatusCodes.Status503ServiceUnavailable, "overloaded_error"),
            };
            if (status is StatusCodes.Status429TooManyRequests or StatusCodes.Status503ServiceUnavailable)
            {
                context.Response.Headers.RetryAfter = settings.QueueTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await WriteError(context, status, type, admission.Message ?? "No console is available.");
            return;
        }

        try
        {
            var console = lease.Console;
            var destination = new UriBuilder(Uri.UriSchemeHttp, console.Host, console.Options.LlamaPort).Uri.ToString();
            context.Response.Headers[ConsoleHeader] = console.Id;
            if (admission.Pool is { } poolAlias)
            {
                context.Response.Headers[PoolHeader] = poolAlias;
            }


            var error = await forwarder.SendAsync(
                context,
                destination,
                upstream.Invoker,
                new ForwarderRequestConfig
                {
                    ActivityTimeout = TimeSpan.FromSeconds(settings.UpstreamTimeoutSeconds),
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                    AllowResponseBuffering = false,
                },
                GatewayTransformer.Instance);

            if (error != ForwarderError.None && !cancellation.IsCancellationRequested)
            {
                var feature = context.Features.Get<IForwarderErrorFeature>();
                logger.LogWarning(feature?.Exception, "Forwarding to console {Console} failed: {Error}", console.Id, error);
                if (!context.Response.HasStarted)
                {
                    await WriteError(context, context.Response.StatusCode, "api_error", $"The console '{console.Id}' failed to answer ({error}).");
                }
            }
        }
        finally
        {
            lease.Release();
        }
    }

    private readonly record struct ModelRead(string? Model, int Status, string? ErrorType, string? Error);

    /// <summary>Finds the top-level <c>model</c> string without consuming the body the forwarder will send on.</summary>
    private static async Task<ModelRead> ReadModelAsync(HttpContext context, long limit, CancellationToken cancellationToken)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limit;
        }

        context.Request.EnableBuffering(bufferThreshold: 1024 * 1024, bufferLimit: limit);
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            context.Request.Body.Position = 0;
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new(null, StatusCodes.Status400BadRequest, "invalid_request_error", "The request body must be a JSON object.");
            }

            if (document.RootElement.TryGetProperty("model", out var model))
            {
                return model.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(model.GetString())
                    ? new(model.GetString()!.Trim(), 0, null, null)
                    : new(null, StatusCodes.Status400BadRequest, "invalid_request_error", "'model' must be a non-empty string.");
            }

            return new(null, 0, null, null);
        }
        catch (JsonException)
        {
            return new(null, StatusCodes.Status400BadRequest, "invalid_request_error", "The request body is not valid JSON.");
        }
        catch (Exception ex) when (ex is IOException or BadHttpRequestException && !cancellationToken.IsCancellationRequested)
        {
            return new(null, StatusCodes.Status413PayloadTooLarge, "invalid_request_error", "The request body is too large.");
        }
    }

    /// <summary>Errors in the shape each API family expects, so SDKs can parse them.</summary>
    private static Task WriteError(HttpContext context, int status, string type, string message)
    {
        context.Response.StatusCode = status;
        object body = context.Request.Path.StartsWithSegments("/v1/messages", StringComparison.Ordinal)
            ? new { type = "error", error = new { type, message } }
            : new { error = new { message, type, code = type } };
        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}

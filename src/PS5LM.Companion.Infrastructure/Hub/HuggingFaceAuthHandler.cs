using System.Net.Http.Headers;

using Microsoft.Extensions.Options;

namespace PS5LM.Companion.Infrastructure.Hub;

/// <summary>Adds the configured Hugging Face token to requests that go to the hub itself.</summary>
internal sealed class HuggingFaceAuthHandler(IOptions<HuggingFaceOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(options.Value.Token) && request.Headers.Authorization is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.Token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

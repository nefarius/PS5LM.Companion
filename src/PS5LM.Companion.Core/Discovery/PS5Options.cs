using System.ComponentModel.DataAnnotations;

using PS5LM.Companion.Core.Domain;

namespace PS5LM.Companion.Core.Discovery;

/// <summary>How to reach one console. Bound from <c>PS5:Consoles:{id}</c>.</summary>
public sealed class PS5Options
{
    /// <summary>Text shown in the dashboard and API. Empty uses the console id.</summary>
    [MaxLength(60)]
    public string? DisplayName { get; set; }

    /// <summary>The console's IP address or host name.</summary>
    [Required]
    public string Host { get; set; } = string.Empty;

    /// <summary>ftpsrv's port.</summary>
    [Range(1, 65535)]
    public int FtpPort { get; set; } = 2121;

    /// <summary>The app's llama-server port; closed until a model is loaded.</summary>
    [Range(1, 65535)]
    public int LlamaPort { get; set; } = 8081;

    /// <summary>ps5-exporter's port.</summary>
    [Range(1, 65535)]
    public int ExporterPort { get; set; } = 9100;

    /// <summary>elfldr's port. Payload Manager forwards launches here and cannot start elfldr itself.</summary>
    [Range(1, 65535)]
    public int ElfldrPort { get; set; } = 9021;

    /// <summary>
    /// Absolute http(s) URL of PS5 Payload Manager. Empty derives <c>http://{Host}:8084</c>.
    /// </summary>
    public string? PayloadManagerUrl { get; set; }

    /// <summary>Forces a variant; <see cref="PS5Variant.Unknown"/> detects it from the installed title folders.</summary>
    public PS5Variant Variant { get; set; } = PS5Variant.Unknown;

    /// <summary>Seconds between status refreshes.</summary>
    [Range(1, 300)]
    public int PollIntervalSeconds { get; set; } = 3;

    /// <summary>How long an installation lookup is reused, in seconds.</summary>
    [Range(0, 3600)]
    public int DiscoveryCacheSeconds { get; set; } = 30;

    /// <summary>How long after a load request the lifecycle stays "loading" while port 8081 is still closed.</summary>
    [Range(1, 3600)]
    public int LoadGraceSeconds { get; set; } = 600;

    /// <summary>The Payload Manager base address, derived from <see cref="Host"/> when <see cref="PayloadManagerUrl"/> is empty.</summary>
    public Uri PayloadManagerBaseAddress()
    {
        if (string.IsNullOrWhiteSpace(PayloadManagerUrl))
        {
            return new UriBuilder(Uri.UriSchemeHttp, Host, PayloadDependencies.DefaultPayloadManagerPort).Uri;
        }

        if (!Uri.TryCreate(PayloadManagerUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("PayloadManagerUrl must be an absolute http or https URL.");
        }

        var builder = new UriBuilder(uri);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }
}
